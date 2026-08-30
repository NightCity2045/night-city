// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server.Popups;
using Content.Server.Preferences.Managers;
using Content.Server.Stack;
using Content.Server._NC.Bank.Cash;
using Content.Server._NC.LivingCity.Economy.Cash;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Bank.Budget;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.Access.Systems;
using Content.Shared.IdentityManagement;
using Content.Shared.Interaction;
using Content.Shared.Roles;
using Content.Shared.Stacks;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Asynchronous;

namespace Content.Server._NC.Bank.Budget;

/// <summary>
/// Server-authoritative cash terminal for one persistent organization budget.
/// Both ID access and the character's active management position are verified for every mutation.
/// </summary>
public sealed partial class NCOrganizationBudgetConsoleSystem : EntitySystem
{
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private NCBankSystem _bank = default!;
    [Dependency] private NCCashTerminalEscrowSystem _cashEscrow = default!;
    [Dependency] private NCCashHandoffSystem _cashHandoffs = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IdentitySystem _identity = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private StackSystem _stacks = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private ITaskManager _taskManager = default!;

    private readonly Dictionary<EntityUid, EntityUid> _activeUsers = new();
    private readonly HashSet<EntityUid> _processing = new();
    private readonly HashSet<Task> _inFlightCashOperations = [];

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
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        if (_inFlightCashOperations.Count != 0)
            args.AddPendingWrite(Task.WhenAll(_inFlightCashOperations.ToArray()));
    }

    private void OnInteractUsing(EntityUid uid, NCOrganizationBudgetConsoleComponent component,
        InteractUsingEvent args)
    {
        // Item insertion is synchronous ECS work. The durable deposit still revalidates the active manager job after
        // the button press, so possessing console access alone cannot mutate the organization account.
        if (_processing.Contains(uid) || !_access.IsAllowed(args.User, uid) || Deleted(args.Used) ||
            !TryComp<StackComponent>(args.Used, out var stack) ||
            stack.StackTypeId != _bank.Configuration.CurrencyStack ||
            !_containers.TryGetContainer(uid, NCOrganizationBudgetConsoleComponent.CashSlotId,
                out var cashContainer))
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
            await UpdateUiAsync(uid, user);
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
        await UpdateUiAsync(uid, user);
    }

    private void OnUiClosed(EntityUid uid, NCOrganizationBudgetConsoleComponent component, BoundUIClosedEvent args)
    {
        if (args.Actor is not { Valid: true } user)
            return;

        _activeUsers.Remove(uid);
    }

    private void OnDeposit(EntityUid uid, NCOrganizationBudgetConsoleComponent component,
        NCOrganizationBudgetDepositMessage args)
    {
        if (args.Actor is not { Valid: true } user || _activeUsers.GetValueOrDefault(uid) != user)
            return;

        var reason = args.Reason.Trim();
        if (!IsValidReason(reason, component))
        {
            _popup.PopupEntity(Loc.GetString("nc-budget-reason-required"), uid, user);
            return;
        }

        if (!_processing.Add(uid))
            return;

        TrackCashOperation(DepositAsync(uid, user, component.MaximumTransaction, reason));
    }

    private async Task DepositAsync(
        EntityUid uid,
        EntityUid user,
        int maximumTransaction,
        string reason)
    {
        NCCashEscrowCapture? capturedCash = null;
        var handoffOwnsCash = false;
        try
        {
            var actor = await GetManagerContextAsync(uid, user);
            if (actor == null)
            {
                await PopupAsync("nc-budget-authority-denied", uid, user);
                return;
            }

            capturedCash = await RunOnMainThreadAsync(() => _cashEscrow.CaptureDeposit(
                uid,
                NCOrganizationBudgetConsoleComponent.CashSlotId,
                _bank.Configuration.CurrencyStack,
                maximumTransaction));
            if (capturedCash == null)
            {
                await PopupAsync("nc-budget-no-cash", uid, user);
                return;
            }
            var amount = capturedCash.Amount;

            if (await _bank.EnsureOrganizationAccountAsync(
                    actor.Department.ID,
                    actor.Department.StartingBudget) == null)
            {
                await RestoreDepositAsync(uid, capturedCash);
                capturedCash = null;
                await PopupAsync("nc-budget-transaction-failed", uid, user);
                return;
            }

            var owner = NCPersistentIdentity.Organization(actor.Department.ID);
            var initiator = NCPartyRef.ForCharacter(actor.CharacterId);
            var handoffId = NCCashHandoffId.New();
            var handoff = await _cashHandoffs.StartAsync(new NCStartCashHandoffRequest(
                Guid.NewGuid(),
                handoffId,
                NCTransactionId.New(),
                owner,
                NCCashHandoffDirection.Deposit,
                amount,
                amount,
                0,
                initiator,
                reason,
                Guid.NewGuid(),
                AuditSource: "organization-budget-cash-deposit"));
            if (handoff.Status is not (NCCashHandoffMutationStatus.Applied or
                NCCashHandoffMutationStatus.Duplicate))
            {
                if (handoff.Status == NCCashHandoffMutationStatus.OutcomeUnknown)
                {
                    handoffOwnsCash = true;
                    await RunOnMainThreadAsync(() => _cashEscrow.Consume(capturedCash));
                }
                else
                {
                    await RestoreDepositAsync(uid, capturedCash);
                }
                capturedCash = null;
                await PopupAsync("nc-budget-transaction-failed", uid, user);
                return;
            }
            handoffOwnsCash = true;
            await RunOnMainThreadAsync(() => _cashEscrow.Consume(capturedCash));
            capturedCash = null;

            var completed = await _cashHandoffs.CompleteAsync(
                handoffId,
                initiator,
                "organization-budget-cash-consumed");
            if (completed.Status is not (NCCashHandoffMutationStatus.Applied or
                NCCashHandoffMutationStatus.Duplicate))
            {
                Log.Error($"Organization deposit handoff {handoffId} is pending durable completion: " +
                          completed.Error);
            }

            await RunOnMainThreadAsync(() =>
            {
                if (!Deleted(uid) && !Deleted(user))
                {
                    _popup.PopupEntity(
                        Loc.GetString("nc-budget-deposit-success", ("amount", amount)),
                        uid,
                        user);
                }
            });
        }
        catch (Exception exception)
        {
            if (capturedCash != null)
            {
                if (handoffOwnsCash)
                    await RunOnMainThreadAsync(() => _cashEscrow.Consume(capturedCash));
                else
                    await RestoreDepositAsync(uid, capturedCash);
            }

            Log.Error($"Organization budget deposit failed on {uid}: {exception}");
            await PopupAsync("nc-budget-transaction-failed", uid, user);
        }
        finally
        {
            await RunOnMainThreadAsync(() => _processing.Remove(uid));
            await UpdateUiAsync(uid, user);
        }
    }

    private void OnWithdraw(EntityUid uid, NCOrganizationBudgetConsoleComponent component,
        NCOrganizationBudgetWithdrawMessage args)
    {
        if (args.Actor is not { Valid: true } user || _activeUsers.GetValueOrDefault(uid) != user)
            return;

        var reason = args.Reason.Trim();
        if (!IsValidReason(reason, component))
        {
            _popup.PopupEntity(Loc.GetString("nc-budget-reason-required"), uid, user);
            return;
        }

        if (args.Amount <= 0 || args.Amount > component.MaximumTransaction)
        {
            _popup.PopupEntity(Loc.GetString("nc-budget-invalid-amount"), uid, user);
            return;
        }

        if (!_processing.Add(uid))
            return;

        TrackCashOperation(WithdrawAsync(uid, user, args.Amount, reason));
    }

    private async Task WithdrawAsync(
        EntityUid uid,
        EntityUid user,
        int amount,
        string reason)
    {
        NCCashHandoffId handoffId = default;
        NCPartyRef owner = default;
        NCPartyRef initiator = default;
        NCCashEscrowCapture? stagedCash = null;
        var handoffStarted = false;
        var handoffCompleted = false;
        try
        {
            var actor = await GetManagerContextAsync(uid, user);
            if (actor == null)
            {
                await PopupAsync("nc-budget-authority-denied", uid, user);
                return;
            }

            if (await _bank.EnsureOrganizationAccountAsync(
                    actor.Department.ID,
                    actor.Department.StartingBudget) == null)
            {
                await PopupAsync("nc-budget-transaction-failed", uid, user);
                return;
            }

            owner = NCPersistentIdentity.Organization(actor.Department.ID);
            initiator = NCPartyRef.ForCharacter(actor.CharacterId);
            handoffId = NCCashHandoffId.New();
            var handoff = await _cashHandoffs.StartAsync(new NCStartCashHandoffRequest(
                Guid.NewGuid(),
                handoffId,
                NCTransactionId.New(),
                owner,
                NCCashHandoffDirection.Withdrawal,
                -amount,
                amount,
                0,
                initiator,
                reason,
                Guid.NewGuid(),
                AuditSource: "organization-budget-cash-withdrawal"));
            if (handoff.Status == NCCashHandoffMutationStatus.InsufficientFunds)
            {
                await PopupAsync("nc-budget-insufficient-funds", uid, user);
                return;
            }
            if (handoff.Status is not (NCCashHandoffMutationStatus.Applied or
                NCCashHandoffMutationStatus.Duplicate))
            {
                await PopupAsync("nc-budget-transaction-failed", uid, user);
                return;
            }
            handoffStarted = true;

            stagedCash = await RunOnMainThreadAsync(() =>
                _cashEscrow.StageWithdrawal(uid, _bank.Configuration.CurrencyStack, amount));
            if (stagedCash == null)
            {
                var reversed = await _cashHandoffs.ReverseAsync(
                    handoffId,
                    initiator,
                    "organization-budget-cash-spawn-rollback");
                Log.Error($"Organization cash spawn failed on {uid}; reversal={reversed.Status}: " +
                          reversed.Error);
                await PopupAsync("nc-budget-transaction-failed", uid, user);
                return;
            }

            var completed = await _cashHandoffs.CompleteAsync(
                handoffId,
                initiator,
                "organization-budget-cash-delivered");
            if (completed.Status is not (NCCashHandoffMutationStatus.Applied or
                NCCashHandoffMutationStatus.Duplicate))
            {
                await RunOnMainThreadAsync(() => _cashEscrow.Consume(stagedCash));
                stagedCash = null;
                var reversed = await _cashHandoffs.ReverseAsync(
                    handoffId,
                    initiator,
                    "organization-budget-cash-delivery-rollback");
                Log.Error($"Organization handoff {handoffId} could not complete; reversal={reversed.Status}: " +
                          (completed.Error ?? reversed.Error));
                await PopupAsync("nc-budget-transaction-failed", uid, user);
                return;
            }
            handoffCompleted = true;

            var released = await RunOnMainThreadAsync(() => _cashEscrow.ReleaseWithdrawal(uid, stagedCash));
            if (!released)
            {
                await RunOnMainThreadAsync(() => _cashEscrow.Consume(stagedCash));
                stagedCash = null;
                Log.Error($"Organization handoff {handoffId} completed but its escrowed cash could not be released.");
                await PopupAsync("nc-budget-transaction-failed", uid, user);
                return;
            }
            stagedCash = null;

            await RunOnMainThreadAsync(() =>
            {
                if (!Deleted(uid) && !Deleted(user))
                    _popup.PopupEntity(Loc.GetString("nc-budget-withdraw-success", ("amount", amount)), uid, user);
            });
        }
        catch (Exception exception)
        {
            if (stagedCash != null)
                await RunOnMainThreadAsync(() => _cashEscrow.Consume(stagedCash));
            if (handoffStarted && !handoffCompleted)
            {
                var reversed = await _cashHandoffs.ReverseAsync(
                    handoffId,
                    initiator,
                    "organization-budget-cash-exception-rollback");
                Log.Error($"Organization exception rollback for handoff {handoffId}: {reversed.Status}: " +
                          reversed.Error);
            }

            Log.Error($"Organization budget withdrawal failed on {uid}: {exception}");
            await PopupAsync("nc-budget-transaction-failed", uid, user);
        }
        finally
        {
            await RunOnMainThreadAsync(() => _processing.Remove(uid));
            await UpdateUiAsync(uid, user);
        }
    }

    private async Task UpdateUiAsync(EntityUid uid, EntityUid user)
    {
        var context = await RunOnMainThreadAsync(() => CaptureUiContext(uid, user));
        if (context == null)
            return;

        var department = context.Department;
        var account = await _bank.EnsureOrganizationAccountAsync(department.ID, department.StartingBudget);
        if (account == null)
            return;

        var transactions = await _bank.GetOrganizationTransactionsAsync(department.ID, context.HistoryLimit);
        var canManage = await GetManagerContextAsync(uid, user) != null;
        await RunOnMainThreadAsync(() =>
        {
            if (Deleted(uid) || Deleted(user))
                return;

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
            if (_containers.TryGetContainer(uid, NCOrganizationBudgetConsoleComponent.CashSlotId,
                    out var cashContainer) && cashContainer.ContainedEntities.Count > 0 &&
                TryComp<StackComponent>(cashContainer.ContainedEntities[0], out var stack) &&
                stack.StackTypeId == _bank.Configuration.CurrencyStack)
            {
                insertedCash = _stacks.GetCount((cashContainer.ContainedEntities[0], stack));
            }

            _ui.SetUiState(uid, NCOrganizationBudgetConsoleUiKey.Key, new NCOrganizationBudgetConsoleState(
                Loc.GetString(department.Name),
                account.Balance,
                insertedCash,
                canManage,
                summaries));
        });
    }

    private async Task<BudgetActorContext?> GetManagerContextAsync(EntityUid console, EntityUid user)
    {
        var candidate = await RunOnMainThreadAsync(() => CaptureManagerCandidate(console, user));
        if (candidate == null)
            return null;

        var activeJob = await _database.GetNCActiveJobAsync(candidate.CharacterId);
        var authorized = await RunOnMainThreadAsync(() => activeJob != null &&
            _prototypes.HasIndex<JobPrototype>(activeJob) &&
            candidate.Department.NCPersonnelManagers.Contains(new ProtoId<JobPrototype>(activeJob)));
        if (!authorized)
        {
            return null;
        }

        return new BudgetActorContext(candidate.CharacterId, candidate.Name, candidate.Department);
    }

    private BudgetActorContext? CaptureManagerCandidate(EntityUid console, EntityUid user)
    {
        if (Deleted(console) || Deleted(user) || !TryComp<NCOrganizationBudgetConsoleComponent>(console, out var comp) ||
            !_access.IsAllowed(user, console) || !_prototypes.TryIndex(comp.Department, out var department) ||
            !_players.TryGetSessionByEntity(user, out var session) ||
            !_preferences.TryGetSelectedNCCharacterId(session.UserId, out var characterId))
        {
            return null;
        }

        var actorName = _identity.GetIdentityShortInfo(user, user) ?? MetaData(user).EntityName;
        return new BudgetActorContext(characterId, actorName, department);
    }

    private BudgetUiContext? CaptureUiContext(EntityUid uid, EntityUid user)
    {
        if (Deleted(uid) || Deleted(user) || _activeUsers.GetValueOrDefault(uid) != user ||
            !TryComp<NCOrganizationBudgetConsoleComponent>(uid, out var component) ||
            !_prototypes.TryIndex(component.Department, out var department))
        {
            return null;
        }

        return new BudgetUiContext(department, component.HistoryLimit);
    }

    private async Task RestoreDepositAsync(EntityUid uid, NCCashEscrowCapture capture)
    {
        var restored = await RunOnMainThreadAsync(() => _cashEscrow.RestoreDeposit(
            uid,
            NCOrganizationBudgetConsoleComponent.CashSlotId,
            capture));
        if (restored)
            return;

        await RunOnMainThreadAsync(() => _cashEscrow.Consume(capture));
        Log.Error($"Organization console {uid} could not restore a rejected {capture.Amount}-credit deposit.");
    }

    private Task PopupAsync(string locale, EntityUid uid, EntityUid user)
    {
        return RunOnMainThreadAsync(() =>
        {
            if (!Deleted(uid) && !Deleted(user))
                _popup.PopupEntity(Loc.GetString(locale), uid, user);
        });
    }

    private void TrackCashOperation(Task operation)
    {
        _inFlightCashOperations.Add(operation);
        ObserveCashOperation(operation);
    }

    private async void ObserveCashOperation(Task operation)
    {
        try
        {
            await operation;
        }
        catch (Exception exception)
        {
            Log.Error($"Unhandled organization cash operation failure: {exception}");
        }
        finally
        {
            _taskManager.RunOnMainThread(() => _inFlightCashOperations.Remove(operation));
        }
    }

    private Task RunOnMainThreadAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _taskManager.RunOnMainThread(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task;
    }

    private Task<T> RunOnMainThreadAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _taskManager.RunOnMainThread(() =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task;
    }

    private static bool IsValidReason(string reason, NCOrganizationBudgetConsoleComponent component)
    {
        return reason.Length > 0 && reason.Length <= component.MaximumReasonLength;
    }

    private sealed record BudgetActorContext(
        NCCharacterId CharacterId,
        string Name,
        DepartmentPrototype Department);

    private sealed record BudgetUiContext(
        DepartmentPrototype Department,
        int HistoryLimit);
}
