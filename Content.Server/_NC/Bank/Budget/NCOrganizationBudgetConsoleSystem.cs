// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.Popups;
using Content.Server.Preferences.Managers;
using Content.Server.Stack;
using Content.Shared._NC.Bank.Budget;
using Content.Shared._NC.Identity;
using Content.Shared.Access.Systems;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Roles;
using Content.Shared.Stacks;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Bank.Budget;

/// <summary>
/// Server-authoritative cash terminal for one persistent organization budget.
/// Both ID access and the character's active management position are verified for every mutation.
/// </summary>
public sealed partial class NCOrganizationBudgetConsoleSystem : EntitySystem
{
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private NCBankSystem _bank = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IdentitySystem _identity = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private StackSystem _stacks = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    private readonly Dictionary<EntityUid, EntityUid> _activeUsers = new();
    private readonly HashSet<(EntityUid Console, EntityUid User)> _processing = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCOrganizationBudgetConsoleComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<NCOrganizationBudgetConsoleComponent, EntInsertedIntoContainerMessage>(OnContainerModified);
        SubscribeLocalEvent<NCOrganizationBudgetConsoleComponent, EntRemovedFromContainerMessage>(OnContainerModified);
        SubscribeLocalEvent<NCOrganizationBudgetConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCOrganizationBudgetConsoleComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<NCOrganizationBudgetConsoleComponent, NCOrganizationBudgetDepositMessage>(OnDeposit);
        SubscribeLocalEvent<NCOrganizationBudgetConsoleComponent, NCOrganizationBudgetWithdrawMessage>(OnWithdraw);
    }

    private async void OnInteractUsing(EntityUid uid, NCOrganizationBudgetConsoleComponent component,
        InteractUsingEvent args)
    {
        if (await GetManagerContextAsync(uid, component, args.User) == null || Deleted(args.Used) ||
            !TryComp<StackComponent>(args.Used, out var stack) ||
            stack.StackTypeId != _bank.Configuration.CurrencyStack ||
            !_containers.TryGetContainer(uid, NCOrganizationBudgetConsoleComponent.CashSlotId, out var cashContainer))
        {
            return;
        }

        if (_containers.Insert(args.Used, cashContainer))
            args.Handled = true;
    }

    private async void OnContainerModified(EntityUid uid, NCOrganizationBudgetConsoleComponent component,
        ContainerModifiedMessage args)
    {
        if (_activeUsers.TryGetValue(uid, out var user))
            await UpdateUiAsync(uid, component, user);
    }

    private async void OnUiOpened(EntityUid uid, NCOrganizationBudgetConsoleComponent component,
        BoundUIOpenedEvent args)
    {
        if (args.Actor is not { Valid: true } user || !_access.IsAllowed(user, uid))
        {
            if (args.Actor is { Valid: true } denied)
                _popup.PopupEntity(Loc.GetString("nc-budget-access-denied"), uid, denied);
            return;
        }

        _activeUsers[uid] = user;
        await UpdateUiAsync(uid, component, user);
    }

    private void OnUiClosed(EntityUid uid, NCOrganizationBudgetConsoleComponent component, BoundUIClosedEvent args)
    {
        if (args.Actor is not { Valid: true } user)
            return;

        _activeUsers.Remove(uid);
        _processing.Remove((uid, user));
    }

    private async void OnDeposit(EntityUid uid, NCOrganizationBudgetConsoleComponent component,
        NCOrganizationBudgetDepositMessage args)
    {
        if (args.Actor is not { Valid: true } user || _activeUsers.GetValueOrDefault(uid) != user)
            return;

        var reason = args.Reason.Trim();
        var actor = await GetManagerContextAsync(uid, component, user);
        if (actor == null || !IsValidReason(reason, component))
        {
            _popup.PopupEntity(Loc.GetString(actor == null
                ? "nc-budget-authority-denied"
                : "nc-budget-reason-required"), uid, user);
            return;
        }

        var operation = (uid, user);
        if (!_processing.Add(operation))
            return;

        var consumedCash = 0;
        try
        {
            if (!_containers.TryGetContainer(uid, NCOrganizationBudgetConsoleComponent.CashSlotId,
                    out var cashContainer) || cashContainer.ContainedEntities.Count == 0)
            {
                _popup.PopupEntity(Loc.GetString("nc-budget-no-cash"), uid, user);
                return;
            }

            var cash = cashContainer.ContainedEntities[0];
            if (!TryComp<StackComponent>(cash, out var stack) ||
                stack.StackTypeId != _bank.Configuration.CurrencyStack)
            {
                return;
            }

            var amount = _stacks.GetCount((cash, stack));
            if (amount <= 0 || amount > component.MaximumTransaction)
            {
                _popup.PopupEntity(Loc.GetString("nc-budget-invalid-amount"), uid, user);
                return;
            }

            // Consume the physical stack before awaiting persistence so the same cash cannot be submitted twice.
            _stacks.SetCount((cash, stack), 0);
            consumedCash = amount;
            var result = await _bank.TryChangeOrganizationBudgetAsync(
                actor.Department.ID,
                actor.Department.StartingBudget,
                amount,
                actor.CharacterId,
                actor.Name,
                reason);
            if (result.Result != NCOrganizationBudgetMutationResult.Success)
            {
                _stacks.SpawnMultipleNextToOrDrop(_bank.Configuration.CurrencyStack, amount, uid);
                consumedCash = 0;
                _popup.PopupEntity(Loc.GetString("nc-budget-transaction-failed"), uid, user);
                return;
            }

            consumedCash = 0;
            _popup.PopupEntity(Loc.GetString("nc-budget-deposit-success", ("amount", amount)), uid, user);
        }
        catch (Exception exception)
        {
            if (consumedCash > 0 && !Deleted(uid))
                _stacks.SpawnMultipleNextToOrDrop(_bank.Configuration.CurrencyStack, consumedCash, uid);

            Log.Error($"Organization budget deposit failed on {uid}: {exception}");
            _popup.PopupEntity(Loc.GetString("nc-budget-transaction-failed"), uid, user);
        }
        finally
        {
            _processing.Remove(operation);
            await UpdateUiAsync(uid, component, user);
        }
    }

    private async void OnWithdraw(EntityUid uid, NCOrganizationBudgetConsoleComponent component,
        NCOrganizationBudgetWithdrawMessage args)
    {
        if (args.Actor is not { Valid: true } user || _activeUsers.GetValueOrDefault(uid) != user)
            return;

        var reason = args.Reason.Trim();
        var actor = await GetManagerContextAsync(uid, component, user);
        if (actor == null || !IsValidReason(reason, component))
        {
            _popup.PopupEntity(Loc.GetString(actor == null
                ? "nc-budget-authority-denied"
                : "nc-budget-reason-required"), uid, user);
            return;
        }

        if (args.Amount <= 0 || args.Amount > component.MaximumTransaction)
        {
            _popup.PopupEntity(Loc.GetString("nc-budget-invalid-amount"), uid, user);
            return;
        }

        var operation = (uid, user);
        if (!_processing.Add(operation))
            return;

        try
        {
            var result = await _bank.TryChangeOrganizationBudgetAsync(
                actor.Department.ID,
                actor.Department.StartingBudget,
                -args.Amount,
                actor.CharacterId,
                actor.Name,
                reason);
            if (result.Result != NCOrganizationBudgetMutationResult.Success)
            {
                var locale = result.Result == NCOrganizationBudgetMutationResult.InsufficientFunds
                    ? "nc-budget-insufficient-funds"
                    : "nc-budget-transaction-failed";
                _popup.PopupEntity(Loc.GetString(locale), uid, user);
                return;
            }

            try
            {
                _stacks.SpawnMultipleNextToOrDrop(_bank.Configuration.CurrencyStack, args.Amount, uid);
            }
            catch (Exception exception)
            {
                var rollback = await _bank.TryChangeOrganizationBudgetAsync(
                    actor.Department.ID,
                    actor.Department.StartingBudget,
                    args.Amount,
                    actor.CharacterId,
                    actor.Name,
                    "Automatic rollback: cash spawn failed");
                Log.Error($"Organization cash spawn failed on {uid}; rollback={rollback.Result}: {exception}");
                _popup.PopupEntity(Loc.GetString("nc-budget-transaction-failed"), uid, user);
                return;
            }

            _popup.PopupEntity(Loc.GetString("nc-budget-withdraw-success", ("amount", args.Amount)), uid, user);
        }
        catch (Exception exception)
        {
            Log.Error($"Organization budget withdrawal failed on {uid}: {exception}");
            _popup.PopupEntity(Loc.GetString("nc-budget-transaction-failed"), uid, user);
        }
        finally
        {
            _processing.Remove(operation);
            await UpdateUiAsync(uid, component, user);
        }
    }

    private async Task UpdateUiAsync(EntityUid uid, NCOrganizationBudgetConsoleComponent component, EntityUid user)
    {
        if (Deleted(uid) || Deleted(user) || !_prototypes.TryIndex(component.Department, out var department))
            return;

        var account = await _bank.EnsureOrganizationAccountAsync(department.ID, department.StartingBudget);
        if (account == null)
            return;

        var transactions = await _bank.GetOrganizationTransactionsAsync(department.ID, component.HistoryLimit);
        var summaries = transactions.Select(value =>
        {
            var reason = value.Reason;
            if (value.Type == NCOrganizationBankTransactionType.Salary &&
                _prototypes.TryIndex<JobPrototype>(reason, out var job))
            {
                reason = job.LocalizedName;
            }

            return new NCOrganizationBudgetTransactionSummary(
                value.Id,
                value.Type.ToString(),
                value.Amount,
                value.BalanceAfter,
                value.ActorName,
                reason,
                value.CreatedAt);
        }).ToList();

        var insertedCash = 0;
        if (_containers.TryGetContainer(uid, NCOrganizationBudgetConsoleComponent.CashSlotId, out var cashContainer) &&
            cashContainer.ContainedEntities.Count > 0 &&
            TryComp<StackComponent>(cashContainer.ContainedEntities[0], out var stack) &&
            stack.StackTypeId == _bank.Configuration.CurrencyStack)
        {
            insertedCash = _stacks.GetCount((cashContainer.ContainedEntities[0], stack));
        }

        var canManage = await GetManagerContextAsync(uid, component, user) != null;
        _ui.SetUiState(uid, NCOrganizationBudgetConsoleUiKey.Key, new NCOrganizationBudgetConsoleState(
            Loc.GetString(department.Name),
            account.Balance,
            insertedCash,
            canManage,
            summaries));
    }

    private async Task<BudgetActorContext?> GetManagerContextAsync(EntityUid console,
        NCOrganizationBudgetConsoleComponent component, EntityUid user)
    {
        if (!_access.IsAllowed(user, console) ||
            !_prototypes.TryIndex(component.Department, out var department) ||
            !_players.TryGetSessionByEntity(user, out var session) ||
            !_preferences.TryGetSelectedNCCharacterId(session.UserId, out var characterId))
        {
            return null;
        }

        var activeJob = await _database.GetNCActiveJobAsync(characterId);
        if (activeJob == null || !_prototypes.HasIndex<JobPrototype>(activeJob) ||
            !department.NCPersonnelManagers.Contains(new ProtoId<JobPrototype>(activeJob)))
        {
            return null;
        }

        var actorName = _identity.GetIdentityShortInfo(user, user) ?? MetaData(user).EntityName;
        return new BudgetActorContext(characterId, actorName, department);
    }

    private static bool IsValidReason(string reason, NCOrganizationBudgetConsoleComponent component)
    {
        return reason.Length > 0 && reason.Length <= component.MaximumReasonLength;
    }

    private sealed record BudgetActorContext(
        NCCharacterId CharacterId,
        string Name,
        DepartmentPrototype Department);
}
