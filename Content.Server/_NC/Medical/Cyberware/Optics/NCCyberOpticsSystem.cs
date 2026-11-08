// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using System.Numerics;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Domains;
using Content.Server._NC.Meta.Runtime;
using Content.Server._NC.Medical.Cyberware.Internal;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Internal;
using Content.Shared._NC.Medical.Cyberware.Optics;
using Content.Shared.ActionBlocker;
using Content.Shared.Body;
using Content.Shared.Flash.Components;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.PowerCell;
using Content.Shared.Verbs;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Cyberware.Optics;

[RegisterComponent]
public sealed partial class NCOpticWearerComponent : Component
{
    public FlashImmunityComponent? ManagedFlash;
    public bool SentView;
    public Vector2? SavedZoom;
    public Vector2? AppliedZoom;
}

/// <summary>Native optical limits apply equally to manual requests and guest programs.</summary>
public sealed partial class NCCyberOpticsSystem : EntitySystem
{
    [Dependency] private Content.Shared.Movement.Systems.SharedContentEyeSystem _contentEye = default!;
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    [Dependency] private NCMetaOpticsSystem _lowLight = default!;
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMedicalImplantSystem _medical = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private PowerCellSystem _cells = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    private readonly HashSet<Entity<MobStateComponent>> _nearby = [];
    private readonly HashSet<Entity<NCOpticRadarShieldComponent>> _shields = [];
    private TimeSpan _nextUpdate;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCOpticWearerComponent, GetVerbsEvent<AlternativeVerb>>(OnVerbs);
        SubscribeLocalEvent<NCOpticRadarComponent, ActivateInWorldEvent>(OnRadarActivate);
        SubscribeLocalEvent<NCOpticWearerComponent, ComponentShutdown>(OnWearerShutdown);
        _devices.RegisterPowerPolicy(ControllerPower);
        InitializeRecording();
        InitializeMeta();
        InitializeSurgery();
        InitializeAppearance();
    }

    public bool TryResolveController(EntityUid controller, out EntityUid eye, out EntityUid body)
    {
        eye = default; body = default;
        if (!HasComp<NCOpticControllerComponent>(controller) ||
            !_containers.TryGetContainingContainer((controller, null, null), out var container) ||
            !HasComp<NCCyberEyeComponent>(container.Owner)) return false;
        eye = container.Owner;
        return _cyberware.TryGetOwningBody(eye, out body);
    }

    public bool TryGetController(EntityUid eye, out EntityUid controller) =>
        _cyberware.TryGetMedicalPart(eye, "NCOpticControllerSlot", out controller) && HasComp<NCOpticControllerComponent>(controller);

    private bool? ControllerPower(EntityUid uid) => !HasComp<NCOpticControllerComponent>(uid) ? null :
        TryResolveController(uid, out var eye, out _) ? IsReady(eye, out _) :
        TryGetServiceBench(uid, out var bench) && _cells.HasCharge(bench, 1);

    public bool HasControllerPower(EntityUid uid) => ControllerPower(uid) == true;

    public bool IsReady(EntityUid eye, out EntityUid body)
    {
        body = default;
        return TryComp<NCCyberEyeComponent>(eye, out _) && TryComp<NCCyberwareComponent>(eye, out var hardware) &&
            hardware.Operational && hardware.PowerGranted && !hardware.ThermalShutdown &&
            !hardware.CrisisBlocksActiveModes && _cyberware.FirmwareAllowsActiveModes(eye) &&
            _cyberware.TryGetOwningBody(eye, out body);
    }

    public bool TryModule(EntityUid eye, NCOpticMode mode, out EntityUid module)
    {
        module = default;
        if (!TryComp<NCCyberwareAssemblyComponent>(eye, out var assembly)) return false;
        foreach (var container in assembly.Parts.Values)
        {
            if (container.ContainedEntity is not { } part || !TryComp<NCOpticModuleComponent>(part, out var option) ||
                option.Cosmetic || option.Mode != mode || !TryComp<NCOrganIntegrityComponent>(part, out var condition) ||
                condition.CurrentIntegrity <= 0) continue;
            module = part; return true;
        }
        return false;
    }

    public float FunctionQuality(EntityUid eye, NCOpticMode mode)
    {
        var quality = _cyberware.QualityPerformance(eye);
        if (TryModule(eye, mode, out var part) && TryComp<NCCyberwarePartComponent>(part, out var physical))
            quality = MathF.Min(quality, _prototypes.Index(_prototypes.Index(physical.Profile).Quality).Performance);
        return quality;
    }

    public bool TrySetMode(EntityUid eye, NCOpticMode mode, bool enabled)
    {
        if (!Enum.IsDefined(mode) || !IsReady(eye, out var body) || !TryComp<NCCyberEyeComponent>(eye, out var optics) ||
            enabled && mode != NCOpticMode.Clear && !TryModule(eye, mode, out _)) return false;
        if (mode == NCOpticMode.Recording) return enabled && TryCapture(eye, body);
        if (mode == NCOpticMode.Radar && enabled && !TryStartRadarFor(body)) return false;
        if (mode == NCOpticMode.AntiFlash) optics.AntiFlash = enabled;
        else if (mode == NCOpticMode.AugmentedReality) optics.AugmentedReality = enabled;
        else optics.ActiveMode = enabled ? mode : NCOpticMode.Clear;
        optics.CommandLease = _timing.CurTime + TimeSpan.FromSeconds(2);
        return true;
    }

    public bool TrySetRuntimeWatts(EntityUid controller, float watts, float seconds = 0)
    {
        if (!float.IsFinite(watts) || watts < 0 || !float.IsFinite(seconds) || seconds < 0) return false;
        if (TryGetServiceBench(controller, out var bench)) return seconds == 0 || _cells.TryUseCharge(bench, watts * seconds);
        if (!TryResolveController(controller, out var eye, out _) ||
            !TryComp<NCCyberEyeComponent>(eye, out var optics)) return false;
        optics.RuntimeLoad = Math.Clamp(watts / 4, 0, .5f);
        return true;
    }

    public void ClearController(EntityUid controller)
    {
        if (TryResolveController(controller, out var eye, out _) && TryComp<NCCyberEyeComponent>(eye, out var optics))
        { optics.ActiveMode = NCOpticMode.Clear; optics.AntiFlash = false; optics.AugmentedReality = false; optics.CommandLease = TimeSpan.Zero; }
    }

    public override void Update(float frameTime)
    {
        if (_timing.CurTime < _nextUpdate) return;
        _nextUpdate = _timing.CurTime + TimeSpan.FromMilliseconds(500);
        var boards = EntityQueryEnumerator<NCOpticControllerComponent>();
        while (boards.MoveNext(out var board, out _)) _devices.TryRestoreOpticIdentity(board);
        UpdateExternalRadars();
        var bodies = new Dictionary<EntityUid, List<Entity<NCCyberEyeComponent>>>();
        var query = EntityQueryEnumerator<NCCyberEyeComponent>();
        while (query.MoveNext(out var uid, out var optics))
        {
            if (IsPaused(uid)) continue;
            if (!_cyberware.TryGetOwningBody(uid, out var body)) { ClearEye(uid, optics); continue; }
            if (!bodies.TryGetValue(body, out var eyes)) bodies[body] = eyes = [];
            eyes.Add((uid, optics)); EnsureComp<NCOpticWearerComponent>(body);
            if (TryGetController(uid, out var controller))
                _runtime.TryPublishEvent(controller, "meta.implants.CyberEye.sampled", process => Sample(process, uid));
        }
        foreach (var (body, eyes) in bodies) UpdateBody(body, eyes);
        var wearers = EntityQueryEnumerator<NCOpticWearerComponent>();
        while (wearers.MoveNext(out var body, out var wearer))
        {
            if (bodies.ContainsKey(body)) continue;
            SetFlash(body, wearer, false);
            ApplyZoom(body, wearer, 1);
            if (wearer.SentView && !_externalViews.ContainsKey(body)) SendView(body, new() { Viewer = GetNetEntity(body), Map = Transform(body).MapID });
            wearer.SentView = false;
        }
    }

    private void UpdateBody(EntityUid body, List<Entity<NCCyberEyeComponent>> eyes)
    {
        var wearer = Comp<NCOpticWearerComponent>(body);
        var view = new NCOpticViewEvent { Viewer = GetNetEntity(body), Map = Transform(body).MapID };
        if (_externalViews.TryGetValue(body, out var external)) view.Contacts = external;
        var flash = false;
        foreach (var eye in eyes.Take(2))
        {
            eye.Comp.Body = body;
            if (!IsReady(eye, out _) || eye.Comp.CommandLease <= _timing.CurTime) { ClearEye(eye, eye.Comp); continue; }
            if (eye.Comp.ActiveMode != NCOpticMode.Clear && !TryModule(eye, eye.Comp.ActiveMode, out _)) eye.Comp.ActiveMode = NCOpticMode.Clear;
            var ownFlash = eye.Comp.AntiFlash && TryModule(eye, NCOpticMode.AntiFlash, out _);
            flash |= ownFlash;
            var lowLight = eye.Comp.ActiveMode == NCOpticMode.LowLight && TryComp<NCMetaOpticsComponent>(eye, out var old) &&
                _lowLight.TryMeasure(body, old, out var ambient) && ambient > .005 / MathF.Max(.5f, FunctionQuality(eye, NCOpticMode.LowLight));
            _lowLight.TrySetManualFilter(eye.Owner, body, lowLight);
            var load = eye.Comp.RuntimeLoad + (eye.Comp.ActiveMode == NCOpticMode.Clear ? 0 : .35f / MathF.Max(.5f, FunctionQuality(eye, eye.Comp.ActiveMode))) +
                (ownFlash ? .1f / MathF.Max(.5f, FunctionQuality(eye, NCOpticMode.AntiFlash)) : 0) + (eye.Comp.AugmentedReality ? .1f : 0);
            _cyberware.TrySetLoad(eye, Math.Clamp(load, 0, 1));
            if (eye.Comp.ActiveMode == NCOpticMode.Infrared) view.Contacts = Observe(body, null, true, FunctionQuality(eye, NCOpticMode.Infrared));
            if (eye.Comp.ActiveMode == NCOpticMode.Radar && FindRadar(body) is { } radar && radar.Comp.ActiveUntil > _timing.CurTime)
                view.Contacts = Observe(body, radar, false, FunctionQuality(eye, NCOpticMode.Radar));
            if (eye.Comp.ActiveMode != NCOpticMode.Clear) { view.Mode = eye.Comp.ActiveMode; view.Quality = FunctionQuality(eye, view.Mode); }
            if (eye.Comp.ActiveMode == NCOpticMode.Telescope &&
                (!TryComp<PhysicsComponent>(body, out var physics) || physics.LinearVelocity.LengthSquared() < .04f))
                view.Zoom = Math.Clamp(eye.Comp.MaximumZoom * MathF.Min(1, FunctionQuality(eye, NCOpticMode.Telescope)), 1, 2);
            if (eye.Comp.AugmentedReality && TryModule(eye, NCOpticMode.AugmentedReality, out _) && _medical.TryGetReading(body, out var sample))
                view.Telemetry = Loc.GetString("nc-medical-implant-reading", ("oxygen", sample.Oxygen), ("perfusion", sample.Perfusion), ("pain", sample.Pain), ("blood", sample.Blood));
        }
        ApplyZoom(body, wearer, view.Zoom);
        SetFlash(body, wearer, flash); SendView(body, view); wearer.SentView = true;
    }

    private void ClearEye(EntityUid uid, NCCyberEyeComponent eye)
    {
        if (eye.Body is { } body && Exists(body)) _lowLight.TrySetManualFilter(uid, body, false);
        eye.ActiveMode = NCOpticMode.Clear; eye.AntiFlash = false; eye.AugmentedReality = false; eye.RuntimeLoad = 0;
        _cyberware.TrySetLoad(uid, 0);
    }

    private void ApplyZoom(EntityUid body, NCOpticWearerComponent wearer, float zoom)
    {
        if (!TryComp<Content.Shared.Movement.Components.ContentEyeComponent>(body, out var eye)) return;
        if (zoom > 1)
        {
            wearer.SavedZoom ??= eye.TargetZoom;
            wearer.AppliedZoom = wearer.SavedZoom.Value / zoom;
            _contentEye.SetZoom(body, wearer.AppliedZoom.Value, eye: eye);
        }
        else if (wearer.SavedZoom is { } saved)
        {
            if (eye.TargetZoom == wearer.AppliedZoom) _contentEye.SetZoom(body, saved, eye: eye);
            wearer.SavedZoom = wearer.AppliedZoom = null;
        }
    }

    private void SetFlash(EntityUid body, NCOpticWearerComponent wearer, bool enabled)
    {
        if (enabled && !HasComp<FlashImmunityComponent>(body)) wearer.ManagedFlash = AddComp<FlashImmunityComponent>(body);
        else if (!enabled && wearer.ManagedFlash is { } added)
        {
            if (TryComp<FlashImmunityComponent>(body, out var current) && ReferenceEquals(current, added)) RemComp(body, current);
            wearer.ManagedFlash = null;
        }
    }
    private void OnWearerShutdown(Entity<NCOpticWearerComponent> ent, ref ComponentShutdown args) { SetFlash(ent, ent.Comp, false); ApplyZoom(ent, ent.Comp, 1); }
    private void SendView(EntityUid body, NCOpticViewEvent view)
    {
        if (TryComp<ActorComponent>(body, out var actor)) RaiseNetworkEvent(view, actor.PlayerSession.Channel);
    }

    private void OnVerbs(Entity<NCOpticWearerComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanInteract || !args.CanAccess) return;
        AddSurgicalVerbs(ent, args.User, args.Verbs);
        if (args.User != ent.Owner) return;
        var user = args.User;
        var query = EntityQueryEnumerator<NCCyberEyeComponent>();
        while (query.MoveNext(out var eye, out _))
        {
            if (!_cyberware.TryGetOwningBody(eye, out var owner) || owner != user || !TryGetController(eye, out var controller)) continue;
            var target = eye; var board = controller;
            if (_devices.CanClaim(board, user)) args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-optics-claim", ("eye", Name(eye))), Act = () => _devices.TryClaim(board, user) });
            AddControllerVerbs(target, board, user, args.Verbs);
            if (TryModule(eye, NCOpticMode.Recording, out var memory))
                args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-optics-album") + ": " + Name(eye), Act = () => SendAlbum(memory, user, 0) });
            foreach (var mode in Enum.GetValues<NCOpticMode>())
            {
                if (mode != NCOpticMode.Clear && !TryModule(eye, mode, out _)) continue;
                var option = mode;
                args.Verbs.Add(new AlternativeVerb { Text = Name(eye) + ": " + Loc.GetString("nc-optics-mode-" + (int) mode), Act = () => TryRequest(target, user, option) });
            }
        }
    }

    private bool TryRequest(EntityUid eye, EntityUid user, NCOpticMode mode)
    {
        if (!_blocker.CanInteract(user, user) || !IsReady(eye, out var body) || body != user || !TryGetController(eye, out var controller)) return false;
        var optics = Comp<NCCyberEyeComponent>(eye);
        var enabled = mode switch { NCOpticMode.AntiFlash => !optics.AntiFlash, NCOpticMode.AugmentedReality => !optics.AugmentedReality, _ => optics.ActiveMode != mode };
        optics.RequestedMode = mode;
        return PublishCommand(controller, (int) mode, enabled, user);
    }
}
