// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Popups;
using System.Threading.Tasks;
using System.Linq;
using Content.Server.Preferences.Managers;
using Content.Server._NC.Bank.Cash;
using Content.Server._NC.LivingCity.Police;
using Content.Server._NC.LivingCity.Economy.Cash;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Server.Stack;
using Content.Shared._NC.Bank.ATM;
using Content.Shared._NC.Bank.Components;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared.Interaction;
using Content.Shared.Stacks;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Player;
using Content.Shared.Database._NC.Police;
using Robust.Shared.Asynchronous;

namespace Content.Server._NC.Bank.ATM;

/// <summary>
/// Exchanges physical credit stacks with persistent character accounts.
/// Authentication and balances are resolved through the database, including for offline account owners.
/// </summary>
public sealed partial class NCAtmSystem : EntitySystem
{
    [Dependency] private NCBankSystem _bank = default!;
    [Dependency] private NCCashTerminalEscrowSystem _cashEscrow = default!;
    [Dependency] private NCCashHandoffSystem _cashHandoffs = default!;
    [Dependency] private NCPoliceFineRegistrySystem _fines = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private StackSystem _stacks = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private ITaskManager _taskManager = default!;

    private readonly Dictionary<EntityUid, EntityUid> _activeUsers = new();
    private readonly Dictionary<(EntityUid Atm, EntityUid User), NCCharacterId> _authenticated = new();
    private readonly HashSet<EntityUid> _processing = new();
    private readonly HashSet<Task> _inFlightCashOperations = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCAtmComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<NCAtmComponent, EntInsertedIntoContainerMessage>(OnContainerModified);
        SubscribeLocalEvent<NCAtmComponent, EntRemovedFromContainerMessage>(OnContainerModified);
        SubscribeLocalEvent<NCAtmComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCAtmComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<NCAtmComponent, NCAtmLoginMessage>(OnLogin);
        SubscribeLocalEvent<NCAtmComponent, NCAtmLogoutMessage>(OnLogout);
        SubscribeLocalEvent<NCAtmComponent, NCAtmWithdrawMessage>(OnWithdraw);
        SubscribeLocalEvent<NCAtmComponent, NCAtmDepositMessage>(OnDeposit);
        SubscribeLocalEvent<NCAtmComponent, NCAtmPayFineMessage>(OnPayFine);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        if (_inFlightCashOperations.Count != 0)
            args.AddPendingWrite(Task.WhenAll(_inFlightCashOperations.ToArray()));
    }

    private void OnInteractUsing(EntityUid uid, NCAtmComponent component, InteractUsingEvent args)
    {
        if (_processing.Contains(uid) || !TryComp<StackComponent>(args.Used, out var stack) ||
            stack.StackTypeId != _bank.Configuration.CurrencyStack ||
            !_containers.TryGetContainer(uid, NCAtmComponent.CashSlotId, out var cashContainer))
        {
            return;
        }

        if (_containers.Insert(args.Used, cashContainer))
            args.Handled = true;
    }

    private async void OnContainerModified(EntityUid uid, NCAtmComponent component, ContainerModifiedMessage args)
    {
        if (_activeUsers.TryGetValue(uid, out var user))
            await UpdateUiAsync(uid, user);
    }

    private async void OnUiOpened(EntityUid uid, NCAtmComponent component, BoundUIOpenedEvent args)
    {
        if (args.Actor is not { Valid: true } user)
            return;

        _activeUsers[uid] = user;
        await UpdateUiAsync(uid, user);
    }

    private void OnUiClosed(EntityUid uid, NCAtmComponent component, BoundUIClosedEvent args)
    {
        if (args.Actor is not { Valid: true } user)
            return;

        _activeUsers.Remove(uid);
        _authenticated.Remove((uid, user));
    }

    private async void OnLogin(EntityUid uid, NCAtmComponent component, NCAtmLoginMessage args)
    {
        if (args.Actor is not { Valid: true } user || _activeUsers.GetValueOrDefault(uid) != user)
            return;

        var account = await _bank.AuthenticateAsync(args.AccountNumber, args.Pin);
        if (account == null)
        {
            await PopupAsync("nc-atm-invalid-credentials", uid, user);
            return;
        }

        await RunOnMainThreadAsync(() => _authenticated[(uid, user)] = account.CharacterId);
        await UpdateUiAsync(uid, user);
    }

    private async void OnLogout(EntityUid uid, NCAtmComponent component, NCAtmLogoutMessage args)
    {
        if (args.Actor is not { Valid: true } user)
            return;

        _authenticated.Remove((uid, user));
        await UpdateUiAsync(uid, user);
    }

    private void OnWithdraw(EntityUid uid, NCAtmComponent component, NCAtmWithdrawMessage args)
    {
        if (args.Actor is not { Valid: true } user ||
            !_authenticated.TryGetValue((uid, user), out var characterId))
        {
            return;
        }

        if (args.Amount <= 0 || args.Amount > component.MaximumTransaction)
        {
            _popup.PopupEntity(Loc.GetString("nc-atm-invalid-amount"), uid, user);
            return;
        }

        if (!_processing.Add(uid))
            return;

        TrackCashOperation(WithdrawAsync(uid, user, characterId, args.Amount));
    }

    private async Task WithdrawAsync(
        EntityUid uid,
        EntityUid user,
        NCCharacterId characterId,
        int amount)
    {
        var party = NCPartyRef.ForCharacter(characterId);
        var handoffId = NCCashHandoffId.New();
        NCCashEscrowCapture? stagedCash = null;
        var handoffStarted = false;
        var handoffCompleted = false;
        try
        {
            var handoff = await _cashHandoffs.StartAsync(new NCStartCashHandoffRequest(
                Guid.NewGuid(),
                handoffId,
                NCTransactionId.New(),
                party,
                NCCashHandoffDirection.Withdrawal,
                -amount,
                amount,
                0,
                party,
                $"ATM cash withdrawal {handoffId}",
                Guid.NewGuid(),
                AuditSource: "atm-cash-withdrawal"));
            if (handoff.Status == NCCashHandoffMutationStatus.InsufficientFunds)
            {
                await PopupAsync("nc-atm-insufficient-funds", uid, user);
                return;
            }
            if (handoff.Status is not (NCCashHandoffMutationStatus.Applied or
                NCCashHandoffMutationStatus.Duplicate))
            {
                await PopupAsync("nc-atm-transaction-failed", uid, user);
                return;
            }
            handoffStarted = true;

            stagedCash = await RunOnMainThreadAsync(() =>
                _cashEscrow.StageWithdrawal(uid, _bank.Configuration.CurrencyStack, amount));
            if (stagedCash == null)
            {
                var reversed = await _cashHandoffs.ReverseAsync(handoffId, party, "atm-cash-spawn-rollback");
                Log.Error($"ATM {uid} could not spawn {amount} credits; reversal={reversed.Status}: " +
                          reversed.Error);
                await PopupAsync("nc-atm-transaction-failed", uid, user);
                return;
            }

            var completed = await _cashHandoffs.CompleteAsync(handoffId, party, "atm-cash-delivered");
            if (completed.Status is not (NCCashHandoffMutationStatus.Applied or
                NCCashHandoffMutationStatus.Duplicate))
            {
                // Staged stacks are private and retain exact EntityUid control until durable completion succeeds.
                await RunOnMainThreadAsync(() => _cashEscrow.Consume(stagedCash));
                stagedCash = null;
                var reversed = await _cashHandoffs.ReverseAsync(handoffId, party, "atm-cash-delivery-rollback");
                Log.Error($"ATM {uid} could not complete handoff {handoffId}; reversal={reversed.Status}: " +
                          (completed.Error ?? reversed.Error));
                await PopupAsync("nc-atm-transaction-failed", uid, user);
                return;
            }
            handoffCompleted = true;

            var released = await RunOnMainThreadAsync(() => _cashEscrow.ReleaseWithdrawal(uid, stagedCash));
            if (!released)
            {
                // Completion is already authoritative, so refunding would duplicate money. Delete every staged UID,
                // including one that was partially released, and leave an auditable fail-closed delivery error.
                await RunOnMainThreadAsync(() => _cashEscrow.Consume(stagedCash));
                stagedCash = null;
                Log.Error($"ATM {uid} completed handoff {handoffId} but could not release its escrowed cash.");
                await PopupAsync("nc-atm-transaction-failed", uid, user);
                return;
            }
            stagedCash = null;

            await RunOnMainThreadAsync(() =>
            {
                if (!Deleted(uid) && !Deleted(user))
                    _popup.PopupEntity(Loc.GetString("nc-atm-withdraw-success", ("amount", amount)), uid, user);
            });
        }
        catch (Exception exception)
        {
            if (stagedCash != null)
                await RunOnMainThreadAsync(() => _cashEscrow.Consume(stagedCash));
            if (handoffStarted && !handoffCompleted)
            {
                var reversed = await _cashHandoffs.ReverseAsync(handoffId, party, "atm-cash-exception-rollback");
                Log.Error($"ATM {uid} exception rollback for handoff {handoffId}: {reversed.Status}: " +
                          reversed.Error);
            }

            Log.Error($"ATM withdrawal failed for character {characterId.Value}: {exception}");
            await PopupAsync("nc-atm-transaction-failed", uid, user);
        }
        finally
        {
            await RunOnMainThreadAsync(() => _processing.Remove(uid));
            await UpdateUiAsync(uid, user);
        }
    }

    private void OnDeposit(EntityUid uid, NCAtmComponent component, NCAtmDepositMessage args)
    {
        if (args.Actor is not { Valid: true } user ||
            !_authenticated.TryGetValue((uid, user), out var characterId) ||
            !_containers.TryGetContainer(uid, NCAtmComponent.CashSlotId, out var cashContainer) ||
            cashContainer.ContainedEntities.Count == 0)
        {
            return;
        }

        if (!_processing.Add(uid))
            return;

        TrackCashOperation(DepositAsync(uid, user, characterId, component.MaximumTransaction,
            component.TaxRate));
    }

    private async Task DepositAsync(
        EntityUid uid,
        EntityUid user,
        NCCharacterId characterId,
        int maximumTransaction,
        float taxRate)
    {
        NCCashEscrowCapture? capturedCash = null;
        var handoffOwnsCash = false;
        try
        {
            capturedCash = await RunOnMainThreadAsync(() => _cashEscrow.CaptureDeposit(
                uid,
                NCAtmComponent.CashSlotId,
                _bank.Configuration.CurrencyStack,
                maximumTransaction));
            if (capturedCash == null)
            {
                await PopupAsync("nc-atm-invalid-amount", uid, user);
                return;
            }

            var total = capturedCash.Amount;
            var tax = Math.Clamp((int) (total * taxRate), 0, total);
            var deposit = total - tax;
            if (deposit <= 0)
            {
                await RestoreDepositAsync(uid, capturedCash);
                capturedCash = null;
                await PopupAsync("nc-atm-invalid-amount", uid, user);
                return;
            }

            var party = NCPartyRef.ForCharacter(characterId);
            var handoffId = NCCashHandoffId.New();
            var handoff = await _cashHandoffs.StartAsync(new NCStartCashHandoffRequest(
                Guid.NewGuid(),
                handoffId,
                NCTransactionId.New(),
                party,
                NCCashHandoffDirection.Deposit,
                deposit,
                total,
                tax,
                party,
                $"ATM cash deposit {handoffId}",
                Guid.NewGuid(),
                AuditSource: "atm-cash-deposit"));
            if (handoff.Status is not (NCCashHandoffMutationStatus.Applied or
                NCCashHandoffMutationStatus.Duplicate))
            {
                if (handoff.Status == NCCashHandoffMutationStatus.OutcomeUnknown)
                {
                    // The ledger may have committed. Consuming the inaccessible stack is fail-closed; a committed
                    // prior-round handoff completes during recovery, while restoring it here could duplicate value.
                    handoffOwnsCash = true;
                    await RunOnMainThreadAsync(() => _cashEscrow.Consume(capturedCash));
                }
                else
                {
                    await RestoreDepositAsync(uid, capturedCash);
                }
                capturedCash = null;
                await PopupAsync("nc-atm-transaction-failed", uid, user);
                return;
            }
            handoffOwnsCash = true;
            await RunOnMainThreadAsync(() => _cashEscrow.Consume(capturedCash));
            capturedCash = null;

            var completed = await _cashHandoffs.CompleteAsync(handoffId, party, "atm-cash-consumed");
            if (completed.Status is not (NCCashHandoffMutationStatus.Applied or
                NCCashHandoffMutationStatus.Duplicate))
            {
                // The ledger credit and pending handoff are already atomic. Do not respawn the consumed cash; the
                // next round deterministically completes a prior-session pending deposit.
                Log.Error($"ATM deposit handoff {handoffId} is pending durable completion: {completed.Error}");
            }

            await RunOnMainThreadAsync(() =>
            {
                if (!Deleted(uid) && !Deleted(user))
                {
                    _popup.PopupEntity(
                        Loc.GetString("nc-atm-deposit-success", ("amount", deposit), ("tax", tax)),
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

            Log.Error($"ATM deposit failed for character {characterId.Value}: {exception}");
            await PopupAsync("nc-atm-transaction-failed", uid, user);
        }
        finally
        {
            await RunOnMainThreadAsync(() => _processing.Remove(uid));
            await UpdateUiAsync(uid, user);
        }
    }

    private void OnPayFine(EntityUid uid, NCAtmComponent component, NCAtmPayFineMessage args)
    {
        if (args.Actor is not { Valid: true } user ||
            !_authenticated.TryGetValue((uid, user), out var characterId) || !args.FineId.IsValid)
            return;

        if (!_processing.Add(uid))
            return;

        TrackCashOperation(PayFineAsync(uid, user, characterId, args.FineId));
    }

    private async Task PayFineAsync(
        EntityUid uid,
        EntityUid user,
        NCCharacterId characterId,
        Content.Shared._NC.LivingCity.Police.NCPoliceFineId fineId)
    {
        try
        {
            var result = await _fines.PayAsync(fineId, NCPartyRef.ForCharacter(characterId));
            var locale = result.Result switch
            {
                NCPoliceFinePaymentResult.Success => "nc-atm-fine-paid",
                NCPoliceFinePaymentResult.InsufficientFunds => "nc-atm-insufficient-funds",
                _ => "nc-atm-fine-payment-failed",
            };
            await PopupAsync(locale, uid, user);
        }
        catch (Exception exception)
        {
            Log.Error($"ATM fine payment failed for character {characterId.Value}: {exception}");
            await PopupAsync("nc-atm-transaction-failed", uid, user);
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

        var ownAccountNumber = string.Empty;
        if (context.OwnCharacterId is { } ownCharacterId)
            ownAccountNumber = (await _bank.GetAccountAsync(ownCharacterId))?.AccountNumber ?? string.Empty;

        var accountNumber = string.Empty;
        long balance = 0;
        var loggedIn = false;
        var fines = new List<NCAtmFineSummary>();
        if (context.AuthenticatedCharacterId is { } authenticatedCharacter)
        {
            var account = await _bank.GetAccountAsync(authenticatedCharacter);
            if (account != null)
            {
                loggedIn = true;
                accountNumber = account.AccountNumber;
                balance = account.Balance;
                var now = context.ActiveTime;
                fines = (await _fines.GetPageAsync(100, NCPartyRef.ForCharacter(authenticatedCharacter)))
                    .Where(value => _fines.EffectiveStatus(value.Snapshot, now) is
                        NCPoliceFineStatus.Issued or NCPoliceFineStatus.Overdue)
                    .Select(value => new NCAtmFineSummary(
                        value.Snapshot.FineId,
                        value.Snapshot.Article,
                        value.Snapshot.Reason,
                        value.Snapshot.Amount,
                        _fines.EffectiveStatus(value.Snapshot, now),
                        TimeSpan.FromTicks(value.Snapshot.DueAt.Ticks - now.Ticks)))
                    .ToList();
            }
            else
            {
                await RunOnMainThreadAsync(() => _authenticated.Remove((uid, user)));
            }
        }

        await RunOnMainThreadAsync(() =>
        {
            if (Deleted(uid) || Deleted(user) || !TryComp<NCAtmComponent>(uid, out var component))
                return;

            var depositAmount = 0;
            if (_containers.TryGetContainer(uid, NCAtmComponent.CashSlotId, out var cashContainer) &&
                cashContainer.ContainedEntities.Count > 0 &&
                TryComp<StackComponent>(cashContainer.ContainedEntities[0], out var stack) &&
                stack.StackTypeId == _bank.Configuration.CurrencyStack)
            {
                depositAmount = _stacks.GetCount((cashContainer.ContainedEntities[0], stack));
            }

            _ui.SetUiState(uid, NCAtmUiKey.Key, new NCAtmBoundUserInterfaceState(
                balance,
                accountNumber,
                loggedIn,
                component.TaxRate,
                depositAmount,
                ownAccountNumber,
                fines));
        });
    }

    private AtmUiContext? CaptureUiContext(EntityUid uid, EntityUid user)
    {
        if (Deleted(uid) || Deleted(user) || !HasComp<NCAtmComponent>(uid) ||
            _activeUsers.GetValueOrDefault(uid) != user)
            return null;

        NCCharacterId? ownCharacterId = null;
        if (_players.TryGetSessionByEntity(user, out var playerSession) &&
            _preferences.TryGetSelectedNCCharacterId(playerSession.UserId, out var selected))
        {
            ownCharacterId = selected;
        }

        NCCharacterId? authenticated = _authenticated.TryGetValue((uid, user), out var authenticatedCharacter)
            ? authenticatedCharacter
            : null;
        var now = _clock.TryGetCurrentTime(out _, out var activeTime)
            ? activeTime
            : Content.Shared._NC.Persistence.Time.NCActiveWorldTime.Zero;
        return new AtmUiContext(ownCharacterId, authenticated, now);
    }

    private async Task RestoreDepositAsync(EntityUid uid, NCCashEscrowCapture capture)
    {
        var restored = await RunOnMainThreadAsync(() =>
            _cashEscrow.RestoreDeposit(uid, NCAtmComponent.CashSlotId, capture));
        if (restored)
            return;

        // Never let a failed escrow restore surface later as a second copy. The exact captured entities are removed.
        await RunOnMainThreadAsync(() => _cashEscrow.Consume(capture));
        Log.Error($"ATM {uid} could not restore a rejected {capture.Amount}-credit deposit from escrow.");
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
            Log.Error($"Unhandled ATM operation failure: {exception}");
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

    private sealed record AtmUiContext(
        NCCharacterId? OwnCharacterId,
        NCCharacterId? AuthenticatedCharacterId,
        Content.Shared._NC.Persistence.Time.NCActiveWorldTime ActiveTime);
}
