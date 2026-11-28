// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.Shared._NC.Dispatch;
using Content.Shared.CartridgeLoader;
using Content.Shared.PDA;

namespace Content.Server._NC.Dispatch;

/// <summary>
/// The PDA programs: the emergency program every resident has (calls and the Trauma subscription) and the
/// dispatcher program of NCPD and Trauma Team members (the service's incidents, take and close).
/// </summary>
public sealed partial class NCDispatchSystem
{
    private void InitializeCartridges()
    {
        SubscribeLocalEvent<NCEmergencyCartridgeComponent, CartridgeUiReadyEvent>(OnEmergencyReady);
        SubscribeLocalEvent<NCEmergencyCartridgeComponent, CartridgeMessageEvent>(OnEmergencyMessage);
        SubscribeLocalEvent<NCDispatchCartridgeComponent, CartridgeUiReadyEvent>(OnDispatchReady);
        SubscribeLocalEvent<NCDispatchCartridgeComponent, CartridgeMessageEvent>(OnDispatchMessage);
    }

    private EntityUid? GetActor(EntityUid loader)
    {
        return _ui.GetActors(loader, PdaUiKey.Key).Select(uid => (EntityUid?) uid).FirstOrDefault();
    }

    private void OnEmergencyReady(Entity<NCEmergencyCartridgeComponent> ent, ref CartridgeUiReadyEvent args)
    {
        UpdateEmergency(args.Loader, GetActor(args.Loader));
    }

    private void OnDispatchReady(Entity<NCDispatchCartridgeComponent> ent, ref CartridgeUiReadyEvent args)
    {
        UpdateDispatch(args.Loader, GetActor(args.Loader));
    }

    private void OnEmergencyMessage(EntityUid uid, NCEmergencyCartridgeComponent component, CartridgeMessageEvent args)
    {
        switch (args)
        {
            case NCEmergencyCallMessage call:
                TryCall(args.Actor, call.Service, call.Text);
                break;
            case NCEmergencyCancelMessage cancel:
                TryCancel(args.Actor, cancel.Service);
                break;
            case NCTraumaSubscribeMessage subscribe:
                TrySubscribe(args.Actor, subscribe.Plan);
                break;
            case NCTraumaUnsubscribeMessage:
                TryUnsubscribe(args.Actor);
                break;
            default:
                return;
        }

        _dirty = true;
        UpdateEmergency(GetEntity(args.LoaderUid), args.Actor);
    }

    private void OnDispatchMessage(EntityUid uid, NCDispatchCartridgeComponent component, CartridgeMessageEvent args)
    {
        switch (args)
        {
            case NCDispatchCartridgeAcceptMessage accept:
                TryAccept(args.Actor, accept.Id);
                break;
            case NCDispatchCartridgeCloseMessage close:
                TryClose(args.Actor, close.Id);
                break;
            default:
                return;
        }

        _dirty = true;
        UpdateDispatch(GetEntity(args.LoaderUid), args.Actor);
    }

    /// <summary>Pushes fresh states to every open emergency and dispatcher program.</summary>
    private void RefreshCartridges()
    {
        var emergency = EntityQueryEnumerator<NCEmergencyCartridgeComponent, CartridgeComponent>();
        while (emergency.MoveNext(out var uid, out _, out var cartridge))
        {
            if (IsActive(uid, cartridge, out var loader))
                UpdateEmergency(loader, GetActor(loader));
        }

        var dispatch = EntityQueryEnumerator<NCDispatchCartridgeComponent, CartridgeComponent>();
        while (dispatch.MoveNext(out var uid, out _, out var cartridge))
        {
            if (IsActive(uid, cartridge, out var loader))
                UpdateDispatch(loader, GetActor(loader));
        }
    }

    private bool IsActive(EntityUid cartridgeUid, CartridgeComponent cartridge, out EntityUid loader)
    {
        loader = default;
        if (cartridge.LoaderUid is not { } loaderUid ||
            !TryComp<CartridgeLoaderComponent>(loaderUid, out var loaderComp) ||
            loaderComp.ActiveProgram != cartridgeUid ||
            !_ui.IsUiOpen(loaderUid, PdaUiKey.Key))
        {
            return false;
        }

        loader = loaderUid;
        return true;
    }

    private void UpdateEmergency(EntityUid loader, EntityUid? actor)
    {
        var services = new List<NCEmergencyServiceView>();
        var plans = new List<NCTraumaPlanView>();
        if (actor is not { } user)
        {
            _cartridgeLoader.UpdateCartridgeUiState(loader, new NCEmergencyCartridgeUiState(false, 0, services, plans, null));
            return;
        }

        foreach (var service in _prototypes.EnumeratePrototypes<NCEmergencyServicePrototype>().OrderBy(s => s.ID))
        {
            var call = GetCall(user, service.ID);
            services.Add(new NCEmergencyServiceView(
                service.ID,
                Loc.GetString(service.Name),
                GetCallPrice(user, service),
                GetCooldown(user, service.ID),
                call?.Id,
                call?.Status ?? NCDispatchStatus.Closed,
                call?.ResponderName ?? string.Empty));
        }

        foreach (var plan in GetPlans())
        {
            plans.Add(new NCTraumaPlanView(
                plan.ID,
                Loc.GetString(plan.Name),
                Loc.GetString(plan.Description),
                plan.Price,
                (int) Math.Round(plan.ResponseTime.TotalMinutes)));
        }

        var hasAccount = _bank.TryGetCharacterAccount(user, out var account);
        _cartridgeLoader.UpdateCartridgeUiState(loader, new NCEmergencyCartridgeUiState(
            hasAccount,
            hasAccount ? account.Balance : 0,
            services,
            plans,
            GetPlan(user)?.ID));
    }

    private void UpdateDispatch(EntityUid loader, EntityUid? actor)
    {
        if (actor is not { } user || !TryGetService(user, out var service))
        {
            _cartridgeLoader.UpdateCartridgeUiState(loader, new NCDispatchCartridgeUiState(false, string.Empty, []));
            return;
        }

        var now = _timing.CurTime;
        var views = new List<NCDispatchIncidentView>();
        var ordered = _incidents
            .Where(incident => incident.Service == service.ID)
            .OrderBy(incident => incident.Status == NCDispatchStatus.Open ? 0 : 1)
            .ThenByDescending(incident => incident.Plan is { } plan && _prototypes.TryIndex(plan, out var p) ? p.Rank : -1)
            .ThenBy(incident => incident.Created);

        foreach (var incident in ordered)
        {
            views.Add(new NCDispatchIncidentView(
                incident.Id,
                incident.Kind,
                incident.Status,
                incident.CallerName,
                incident.Message,
                incident.LocationText,
                incident.Location.Z,
                now - incident.Created,
                incident.ResponderName,
                incident.Responder == user,
                incident.Plan is { } planId && _prototypes.TryIndex(planId, out var plan) ? Loc.GetString(plan.Name) : string.Empty,
                incident.Uncovered,
                incident.Shots));
        }

        _cartridgeLoader.UpdateCartridgeUiState(loader, new NCDispatchCartridgeUiState(true, Loc.GetString(service.Name), views));
    }
}
