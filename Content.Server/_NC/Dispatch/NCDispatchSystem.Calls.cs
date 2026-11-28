// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Dispatch;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Dispatch;

public sealed partial class NCDispatchSystem
{
    private readonly Dictionary<(EntityUid Caller, string Service), TimeSpan> _cooldowns = new();

    /// <summary>What a call to the service costs this caller: free for Trauma subscribers.</summary>
    public long GetCallPrice(EntityUid caller, NCEmergencyServicePrototype service)
    {
        if (service.ID == TraumaService && GetPlan(caller) != null)
            return 0;

        return service.CallPrice;
    }

    /// <summary>Time left before the caller may call the service again.</summary>
    public TimeSpan GetCooldown(EntityUid caller, ProtoId<NCEmergencyServicePrototype> service)
    {
        if (!_cooldowns.TryGetValue((caller, service.Id), out var until))
            return TimeSpan.Zero;

        var left = until - _timing.CurTime;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>A resident calls a service from their PDA. A one-off Trauma call is paid up front.</summary>
    public bool TryCall(EntityUid caller, ProtoId<NCEmergencyServicePrototype> serviceId, string text, bool quiet = false)
    {
        if (!CanCall(caller, serviceId, quiet))
            return false;

        var service = _prototypes.Index(serviceId);
        _mapCoordinates.TryGetCoordinates(caller, out var location);
        var price = GetCallPrice(caller, service);
        var account = _bank.TryGetCharacterAccount(caller, out var found) ? found : null;
        if (price > 0 &&
            (account == null || !_bank.TryPayService(account, service.Account!.Value, price, Loc.GetString(service.Name), caller)))
        {
            return false;
        }

        var message = text.Trim();
        if (message.Length > MaximumMessageLength)
            message = message[..MaximumMessageLength];
        if (message.Length == 0)
            message = Loc.GetString("nc-dispatch-message-empty");

        var incident = CreateIncident(serviceId, NCDispatchIncidentKind.Call, caller, location, Name(caller), message);
        incident.Subject = caller;
        incident.Caller = caller;
        incident.Client = account;
        incident.Paid = price;
        incident.Uncovered = service.UsesCoverage && !_coverage.IsCovered(location);
        if (serviceId == TraumaService && GetPlan(caller) is { } plan)
        {
            incident.Plan = plan.ID;
            incident.Deadline = incident.Created + plan.ResponseTime;
        }
        else if (price > 0 && service.CallResponseTime is { } responseTime)
        {
            incident.Deadline = incident.Created + responseTime;
        }

        _cooldowns[(caller, serviceId.Id)] = _timing.CurTime + service.CallCooldown;
        Announce(incident);
        if (!quiet)
        {
            _popup.PopupEntity(Loc.GetString(incident.Uncovered ? "nc-dispatch-call-sent-uncovered" : "nc-dispatch-call-sent",
                ("id", incident.Id)), caller, caller);
        }

        return true;
    }

    public bool CanCall(EntityUid caller, ProtoId<NCEmergencyServicePrototype> serviceId, bool quiet = false)
    {
        string? error = null;
        if (!_prototypes.TryIndex(serviceId, out var service))
            error = "nc-dispatch-call-unavailable";
        else if (!_mapCoordinates.TryGetCoordinates(caller, out _))
            error = "nc-dispatch-call-no-signal";
        else if (GetCall(caller, serviceId) != null)
            error = "nc-dispatch-call-already";
        else if (GetCooldown(caller, serviceId) > TimeSpan.Zero)
            error = "nc-dispatch-call-cooldown";
        else if (GetCallPrice(caller, service) > 0)
        {
            var price = GetCallPrice(caller, service);
            if (service.Account == null)
                error = "nc-dispatch-call-unavailable";
            else if (!_bank.TryGetCharacterAccount(caller, out var account))
                error = "nc-dispatch-call-no-account";
            else if (account.Balance < price)
                error = "nc-dispatch-call-no-money";
        }

        if (error == null)
            return true;

        if (!quiet)
            _popup.PopupEntity(Loc.GetString(error), caller, caller);
        return false;
    }

    /// <summary>The caller calls their call off. A paid call not reached yet is paid back.</summary>
    public bool TryCancel(EntityUid caller, ProtoId<NCEmergencyServicePrototype> service)
    {
        if (GetCall(caller, service) is not { } incident)
            return false;

        Close(incident, "nc-dispatch-closed-cancelled");
        return true;
    }

    /// <summary>A service member takes an open incident.</summary>
    public bool TryAccept(EntityUid responder, int id, bool quiet = false)
    {
        if (!CanAccept(responder, id, quiet) || GetIncident(id) is not { } incident)
            return false;

        incident.Status = NCDispatchStatus.Accepted;
        incident.Responder = responder;
        incident.ResponderName = Name(responder);
        incident.Changed = _timing.CurTime;
        _dirty = true;
        TellCaller(incident, Loc.GetString("nc-dispatch-accepted", ("id", incident.Id), ("responder", incident.ResponderName)));
        return true;
    }

    public bool CanAccept(EntityUid responder, int id, bool quiet = false)
    {
        string? error = null;
        if (GetIncident(id) is not { } incident || !IsMember(responder, incident.Service))
            error = "nc-dispatch-not-found";
        else if (incident.Status != NCDispatchStatus.Open)
            error = "nc-dispatch-already-taken";

        if (error == null)
            return true;

        if (!quiet)
            _popup.PopupEntity(Loc.GetString(error), responder, responder);
        return false;
    }

    /// <summary>A service member closes an incident of their service.</summary>
    public bool TryClose(EntityUid user, int id, bool quiet = false)
    {
        if (GetIncident(id) is not { } incident || !IsMember(user, incident.Service))
        {
            if (!quiet)
                _popup.PopupEntity(Loc.GetString("nc-dispatch-not-found"), user, user);
            return false;
        }

        Close(incident, "nc-dispatch-closed");
        return true;
    }
}
