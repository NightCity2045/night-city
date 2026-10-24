// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Content.Meta.Hosting;
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta.UI;
using Robust.Shared.Asynchronous;
using Robust.Shared.Player;

namespace Content.Server._NC.Meta.Applications;

public sealed record NCMetaInvoicePaymentResult(bool Success, string NoticeKey);
/// <summary>The trusted payment adapter must check canCommit on the main thread immediately before its durable write.</summary>
public delegate Task<NCMetaInvoicePaymentResult> NCMetaInvoicePayment(EntityUid actor, Guid invoiceId, string invoiceHash,
    long amount, string payee, Guid confirmationId, Func<bool> canCommit);

public sealed partial class NCMetaApplicationSystem
{
    private sealed class InvoicePrompt(Session session, Guid confirmationId, Guid invoiceId, string hash, long amount,
        string payee, long expires, NCMetaInvoicePayment payment)
    {
        public readonly Session Session = session;
        public readonly Guid ConfirmationId = confirmationId;
        public readonly Guid InvoiceId = invoiceId;
        public readonly string Hash = hash;
        public readonly long Amount = amount;
        public readonly string Payee = payee;
        public readonly long Expires = expires;
        public readonly NCMetaInvoicePayment Payment = payment;
        public bool Executing;
    }
    [Dependency] private ITaskManager _invoiceTasks = default!;
    private readonly Dictionary<Guid, InvoicePrompt> _invoicePrompts = [];
    private readonly Dictionary<Guid, Guid> _invoiceBySession = [];
    private readonly Dictionary<ICommonSession, MetaRateLimit> _invoiceRates = [];
    private readonly Dictionary<ICommonSession, MetaRateLimit> _invoiceDecisionRates = [];

    public bool TryResolveAppActor(NCMetaRuntimeInstance runtime, MetaValue userRef, MetaValue sessionRef, out EntityUid actor)
    {
        actor = default;
        if (!runtime.Process.TryGetOpaqueToken(userRef, out var userToken) || !runtime.Process.TryGetOpaqueToken(sessionRef, out var sessionToken) ||
            !_persistence.TryCaptureTime(out var epoch, out _))
            return false;
        foreach (var session in _sessions.Values)
        {
            if (session.SessionToken != sessionToken || session.UserToken != userToken || session.UserRef != userRef || session.SessionRef != sessionRef ||
                session.Registration.Host != runtime.Host || session.Registration.RuntimeGeneration != runtime.Generation ||
                userRef.TypeId != session.Registration.Application.UserRefTypeId || sessionRef.TypeId != session.Registration.Application.SessionRefTypeId ||
                session.WorldEpoch != epoch || runtime.WorldEpoch != epoch || runtime.InvocationActor != session.User || !session.Busy || !CanUse(session) ||
                !_runtime.TryGetGeneration(runtime.Host, runtime.Owner, runtime.Generation, out var live) || !ReferenceEquals(live, runtime))
                continue;
            actor = session.User;
            return true;
        }
        return false;
    }

    /// <summary>Returns only the currently executing, server-admitted action's idempotency identity.</summary>
    public bool TryGetAppActionId(NCMetaRuntimeInstance runtime, MetaValue userRef, MetaValue sessionRef, out Guid actionId)
    {
        actionId = Guid.Empty;
        if (!TryResolveAppActor(runtime, userRef, sessionRef, out var actor))
            return false;
        foreach (var session in _sessions.Values)
        {
            if (session.User != actor || session.UserRef != userRef || session.SessionRef != sessionRef ||
                session.Registration.Host != runtime.Host || session.Registration.RuntimeGeneration != runtime.Generation ||
                session.Action is not { Executed: false } action)
                continue;
            actionId = action.Id;
            return true;
        }
        return false;
    }

    /// <summary>Called only by the native invoice adapter with authoritative invoice terms. Success means awaiting user consent.</summary>
    public bool TryRequestInvoiceConfirmation(NCMetaRuntimeInstance runtime, MetaValue userRef, MetaValue sessionRef,
        Guid invoiceId, string invoiceHash, long amount, string payee, NCMetaInvoicePayment payment, out Guid confirmationId)
    {
        confirmationId = Guid.Empty;
        if (!TryResolveAppActor(runtime, userRef, sessionRef, out var actor) || invoiceId == Guid.Empty ||
            invoiceHash.Length != 64 || !invoiceHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            amount is <= 0 or > 1000000000 || payee.Length is < 1 or > 128 || Encoding.UTF8.GetByteCount(payee) > 256 ||
            _invoicePrompts.Count >= 128)
            return false;
        var session = _sessions.Values.First(x => x.User == actor && x.SessionRef == sessionRef && x.UserRef == userRef && x.Registration.Host == runtime.Host);
        if (_invoicePrompts.Values.Any(x => x.Session.Id == session.Id) ||
            _invoicePrompts.Values.Count(x => x.Session.Connection == session.Connection) >= 4)
            return false;
        if (!_invoiceRates.TryGetValue(session.Connection, out var rate))
            _invoiceRates.Add(session.Connection, rate = new(2, 1));
        if (!rate.TryConsume(_now)) return false;
        confirmationId = Guid.NewGuid();
        var prompt = new InvoicePrompt(session, confirmationId, invoiceId, invoiceHash, amount, payee, _now + 30000, payment);
        _invoicePrompts.Add(confirmationId, prompt);
        _invoiceBySession[session.Id] = confirmationId;
        _ui.ServerSendUiMessage(session.UiOwner, session.UiKey,
            new NCMetaInvoicePromptMessage(session.Id, confirmationId, invoiceId, invoiceHash, amount, payee, 30), session.User);
        return true;
    }

    private bool CanConfirmInvoice(InvoicePrompt prompt) => _invoicePrompts.TryGetValue(prompt.ConfirmationId, out var current) && ReferenceEquals(current, prompt) &&
        _sessions.TryGetValue(prompt.Session.Id, out var session) && ReferenceEquals(session, prompt.Session) && CanUse(session) &&
        _persistence.TryCaptureTime(out var epoch, out var time) && epoch == session.WorldEpoch && time.Ticks / TimeSpan.TicksPerMillisecond < prompt.Expires;

    private void OnInvoiceDecision(Entity<NCMetaApplicationUiComponent> ent, ref NCMetaInvoiceDecisionMessage args)
    {
        if (!_invoicePrompts.TryGetValue(args.ConfirmationId, out var prompt) || prompt.Session.UiOwner != ent.Owner ||
            prompt.Session.User != args.Actor || prompt.Session.Id != args.SessionId || prompt.InvoiceId != args.InvoiceId ||
            prompt.Hash != args.InvoiceHash || prompt.Amount != args.Amount || prompt.Payee != args.Payee || !CanConfirmInvoice(prompt))
            return;
        if (!_invoiceDecisionRates.TryGetValue(prompt.Session.Connection, out var rate))
            _invoiceDecisionRates.Add(prompt.Session.Connection, rate = new(4, 2));
        if (!rate.TryConsume(_now)) return;
        if (prompt.Executing) { SendInvoiceStatus(prompt, true, false, "nc-meta-invoice-pending"); return; }
        if (!args.Accept)
        { RemoveInvoicePrompt(prompt); SendInvoiceStatus(prompt, false, false, "nc-meta-invoice-cancelled"); return; }
        prompt.Executing = true;
        SendInvoiceStatus(prompt, true, false, "nc-meta-invoice-pending");
        _ = PayInvoiceAsync(prompt);
    }

    private async Task PayInvoiceAsync(InvoicePrompt prompt)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        var result = new NCMetaInvoicePaymentResult(false, "nc-meta-error-busy");
        try
        {
            if (mutation is not null && CanConfirmInvoice(prompt))
                result = await prompt.Payment(prompt.Session.User, prompt.InvoiceId, prompt.Hash, prompt.Amount,
                    prompt.Payee, prompt.ConfirmationId, () => CanConfirmInvoice(prompt)) ?? new(false, "nc-meta-invoice-unknown");
        }
        catch (Exception exception)
        {
            Log.Error($"META invoice payment result could not be confirmed: {exception}");
            result = new(false, "nc-meta-invoice-unknown");
        }
        var completed = result;
        await InvoiceMain(() =>
        {
            RemoveInvoicePrompt(prompt);
            SendInvoiceStatus(prompt, false, completed.Success, completed.NoticeKey);
            if (_sessions.TryGetValue(prompt.Session.Id, out var session)) { session.NeedsView = true; Enqueue(session); }
        });
    }

    private void SendInvoiceStatus(InvoicePrompt prompt, bool pending, bool success, string noticeKey)
    {
        var session = prompt.Session;
        if (Exists(session.UiOwner) && Exists(session.User) && _ui.IsUiOpen(session.UiOwner, session.UiKey, session.User))
            _ui.ServerSendUiMessage(session.UiOwner, session.UiKey,
                new NCMetaInvoiceStatusMessage(session.Id, prompt.ConfirmationId, pending, success, Loc.GetString(noticeKey)), session.User);
    }
    private void RemoveInvoicePrompt(InvoicePrompt prompt)
    {
        _invoicePrompts.Remove(prompt.ConfirmationId);
        if (_invoiceBySession.GetValueOrDefault(prompt.Session.Id) == prompt.ConfirmationId) _invoiceBySession.Remove(prompt.Session.Id);
    }
    private void UpdateSessionInvoice(Session session)
    {
        if (_invoiceBySession.TryGetValue(session.Id, out var id) && _invoicePrompts.TryGetValue(id, out var prompt) && !CanConfirmInvoice(prompt))
        {
            RemoveInvoicePrompt(prompt);
            SendInvoiceStatus(prompt, false, false, "nc-meta-invoice-expired");
        }
    }
    private void CloseInvoices(Session session)
    {
        if (_invoiceBySession.TryGetValue(session.Id, out var id) && _invoicePrompts.TryGetValue(id, out var prompt))
        { RemoveInvoicePrompt(prompt); SendInvoiceStatus(prompt, false, false, "nc-meta-invoice-cancelled"); }
        if (!_sessions.Values.Any(x => x.Connection == session.Connection))
        { _invoiceRates.Remove(session.Connection); _invoiceDecisionRates.Remove(session.Connection); }
    }
    private Task InvoiceMain(Action action)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _invoiceTasks.RunOnMainThread(() => { try { action(); result.SetResult(); } catch (Exception exception) { result.SetException(exception); } });
        return result.Task;
    }
}
