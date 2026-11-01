// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using Content.Server._NC.Meta.Devices;
using Content.Server.Popups;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Meta;
using Content.Shared.Interaction;
using Content.Shared.Verbs;
using Robust.Server.GameObjects;

namespace Content.Server._NC.Meta.Domains;

/// <summary>Physical commissioning and explicit manual takeover. Every action rechecks the acting user.</summary>
public sealed partial class NCMetaCommissioningSystem : EntitySystem
{
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaClinicalSystem _clinical = default!;
    [Dependency] private NCMetaDroneSystem _drones = default!;
    [Dependency] private NCMetaIndustrialSystem _industrial = default!;
    [Dependency] private NCMetaEcuSystem _ecus = default!;
    [Dependency] private NCMetaOpticsSystem _optics = default!;
    [Dependency] private NCMetaDriverAssistanceSystem _assistance = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private PopupSystem _popup = default!;
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCMetaCommissioningToolComponent, AfterInteractEvent>(OnCommission);
        SubscribeLocalEvent<NCMetaClinicalComponent, AfterInteractEvent>(OnPatient);
        SubscribeLocalEvent<NCMetaClinicalComponent, GetVerbsEvent<AlternativeVerb>>(OnClinicalVerbs);
        SubscribeLocalEvent<NCMetaDroneComponent, GetVerbsEvent<AlternativeVerb>>(OnDroneVerbs);
        SubscribeLocalEvent<NCMetaConveyorComponent, GetVerbsEvent<AlternativeVerb>>(OnConveyorVerbs);
        SubscribeLocalEvent<NCMetaEcuComponent, GetVerbsEvent<AlternativeVerb>>(OnEcuVerbs);
        SubscribeLocalEvent<NCMetaOpticsWearerComponent, GetVerbsEvent<AlternativeVerb>>(OnBodyVerbs);
        SubscribeLocalEvent<NCMetaDriverAssistanceComponent, GetVerbsEvent<AlternativeVerb>>(OnAssistanceVerbs);
    }
    private void OnCommission(Entity<NCMetaCommissioningToolComponent> ent, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target is not { } target) return;
        var user = args.User;
        if (ent.Comp.SelectedDevice is { } selected && Exists(selected))
        {
            var applied = HasComp<NCMetaClinicalComponent>(selected) && _clinical.TryBindStationaryScanner(selected, user, target) ||
                HasComp<NCMetaDroneComponent>(selected) && HasComp<NCMetaWaypointComponent>(target) && _drones.TryRegisterWaypoint(selected, user, target) ||
                HasComp<NCMetaConveyorComponent>(selected) && HasComp<NCMetaLoadSensorComponent>(target) && _industrial.TryBindLoadSensor(selected, user, target);
            if (applied)
            {
                args.Handled = true; ent.Comp.SelectedDevice = null;
                _popup.PopupEntity(Loc.GetString("nc-meta-domain-linked"), ent.Owner, user); return;
            }
        }
        if (!_devices.CanManage(target, user) || !(HasComp<NCMetaDroneComponent>(target) || HasComp<NCMetaConveyorComponent>(target) || HasComp<NCMetaClinicalComponent>(target))) return;
        ent.Comp.SelectedDevice = target; args.Handled = true;
        _popup.PopupEntity(Loc.GetString("nc-meta-domain-select-target"), ent.Owner, user);
    }
    private void OnPatient(Entity<NCMetaClinicalComponent> ent, ref AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach || args.Target is not { } patient || !HasComp<NCMedicalBodyComponent>(patient)) return;
        args.Handled = true;
        var bound = _clinical.TryBindPatient(ent.Owner, args.User, patient);
        _popup.PopupEntity(Loc.GetString(bound ? "nc-meta-clinical-patient-bound" : "nc-meta-error-access"), ent.Owner, args.User);
    }
    private bool CanOffer(EntityUid device, ref GetVerbsEvent<AlternativeVerb> args) => args.CanAccess && args.CanInteract && _devices.CanManage(device, args.User);
    private void OnClinicalVerbs(Entity<NCMetaClinicalComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!CanOffer(ent.Owner, ref args)) return;
        var user = args.User; var enabled = ent.Comp.AutomationEnabled;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString(enabled ? "nc-meta-domain-manual" : "nc-meta-domain-automation"),
            Act = () => _clinical.TrySetAutomation(ent.Owner, user, !enabled) });
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-clinical-clear-patient"), Act = () => _clinical.TryBindPatient(ent.Owner, user, null) });
    }
    private void OnDroneVerbs(Entity<NCMetaDroneComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!CanOffer(ent.Owner, ref args)) return;
        var user = args.User; var manual = ent.Comp.ManualOverride;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString(manual ? "nc-meta-domain-automation" : "nc-meta-domain-manual"), Act = () => _drones.TrySetManualControl(ent.Owner, user, !manual) });
        foreach (var (mode, key) in new[]
            {
                (NCMetaDroneMode.Assistant, "nc-meta-drone-assistant"), (NCMetaDroneMode.Autonomous, "nc-meta-drone-autonomous"),
                (NCMetaDroneMode.Maintenance, "nc-meta-drone-maintenance"), (NCMetaDroneMode.Emergency, "nc-meta-drone-emergency")
            })
            if (ent.Comp.Mode != mode)
                args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString(key), Act = () => _drones.TrySetMode(ent.Owner, user, mode) });
        if (ent.Comp.Mode == NCMetaDroneMode.Assistant && ent.Comp.Operator == user)
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-drone-renew-assistant"), Act = () => _drones.TryRenewAssistant(ent.Owner, user) });
    }
    private void OnAssistanceVerbs(Entity<NCMetaDriverAssistanceComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!CanOffer(ent.Owner, ref args)) return;
        var user = args.User; var manual = ent.Comp.ManualOverride;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString(manual ? "nc-meta-assistance-automation" : "nc-meta-assistance-manual"),
            Act = () => _assistance.TrySetManualControl(ent.Owner, user, !manual) });
    }
    private void OnConveyorVerbs(Entity<NCMetaConveyorComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!CanOffer(ent.Owner, ref args)) return;
        var user = args.User; var manual = ent.Comp.ManualOverride;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString(manual ? "nc-meta-domain-automation" : "nc-meta-domain-manual"), Act = () => _industrial.TrySetManualControl(ent.Owner, user, !manual) });
    }
    private void OnEcuVerbs(Entity<NCMetaEcuComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!CanOffer(ent.Owner, ref args)) return;
        var user = args.User; var manual = ent.Comp.ManualOverride;
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString(manual ? "nc-meta-domain-automation" : "nc-meta-domain-manual"), Act = () => _ecus.TrySetManualControl(ent.Owner, user, !manual) });
    }
    private void OnBodyVerbs(Entity<NCMetaOpticsWearerComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (args.User != ent.Owner || !args.CanInteract) return;
        var user = args.User;
        foreach (var module in _optics.GetInstalledModules(ent.Owner).Take(4))
        {
            if (_devices.CanClaim(module, user))
                args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-optics-claim"), Act = () => _devices.TryClaim(module, user) });
            if (!_devices.CanManage(module, user)) continue;
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-optics-workspace"), Act = () => TryOpenOptics(module, user) });
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-optics-low-light"), Act = () => _optics.TrySetManualFilter(module, user, true) });
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-optics-clear"), Act = () => _optics.TrySetManualFilter(module, user, false) });
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-domain-automation"), Act = () => _optics.TryEnableAutomation(module, user) });
        }
    }
    private bool TryOpenOptics(EntityUid module, EntityUid user) => _devices.CanManage(module, user) &&
        _optics.GetInstalledModules(user).Contains(module) && _ui.TryOpenUi(module, NCMetaUiKey.Workspace, user);
}
