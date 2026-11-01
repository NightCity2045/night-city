// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Medical.Humanity;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Abilities;
using Content.Shared._NC.Medical.Function;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.Actions;
using Content.Shared.Body;
using Content.Shared.Hands;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Popups;
using Content.Shared.Tools.Components;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Containers;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Cyberware.Abilities;

/// <summary>Physical capability authority. Only active modules are polled; topology reconciliation is body-local.</summary>
public sealed partial class NCCyberwareAbilitySystem : EntitySystem
{
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    [Dependency] private NCHumanitySystem _humanity = default!;
    [Dependency] private NCMedicalBodySystem _medical = default!;
    [Dependency] private SharedActionsSystem _actions = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private MovementSpeedModifierSystem _movement = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    private readonly HashSet<EntityUid> _active = [];
    private readonly HashSet<EntityUid> _refreshing = [];
    private TimeSpan _nextTick;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCyberwareAbilityComponent, MapInitEvent>(OnModuleInit,
            after: [typeof(InitialBodySystem), typeof(NCCyberwareSystem)]);
        SubscribeLocalEvent<NCCyberwareAbilityComponent, ComponentShutdown>(OnModuleShutdown);
        SubscribeLocalEvent<NCCyberwareAbilityComponent, NCActivateCyberwareAbilityEvent>(OnActivate);
        SubscribeLocalEvent<NCCyberwareAbilityBodyComponent, MapInitEvent>(OnBodyInit,
            after: [typeof(InitialBodySystem), typeof(NCCyberwareSystem)]);
        SubscribeLocalEvent<NCCyberwareAbilityBodyComponent, NCMedicalBodyChangedEvent>(OnBodyChanged);
        SubscribeLocalEvent<NCCyberwareAbilityBodyComponent, NCCyberwareInstalledEvent>(OnInstalled);
        SubscribeLocalEvent<NCCyberwareAbilityBodyComponent, NCCyberwareRemovedEvent>(OnRemoved);
        SubscribeLocalEvent<NCCyberwareAbilityBodyComponent, NCCyberwareStateChangedEvent>(OnStateChanged);
        SubscribeLocalEvent<NCCyberwareAbilityBodyComponent, NCBodyFunctionChangedEvent>(OnFunctionsChanged);
        SubscribeLocalEvent<NCCyberwareAbilityBodyComponent, ComponentShutdown>(OnBodyShutdown);
        SubscribeLocalEvent<NCIntegralCyberwareItemComponent, AttemptMeleeEvent>(OnMeleeAttempt);
        SubscribeLocalEvent<NCIntegralCyberwareItemComponent, ToolUseAttemptEvent>(OnToolAttempt);
    }

    private void OnModuleInit(Entity<NCCyberwareAbilityComponent> module, ref MapInitEvent args)
    {
        GetProfile(module.Comp);
        module.Comp.CooldownObservedAt = _timing.CurTime;
        module.Comp.ItemContainer = _containers.EnsureContainer<ContainerSlot>(module, NCCyberwareAbilityComponent.ContainerId);
    }
    private void OnModuleShutdown(Entity<NCCyberwareAbilityComponent> module, ref ComponentShutdown args)
    {
        var body = module.Comp.Body;
        Stop(module);
        _actions.RemoveAction(module.Comp.Action);
        if (module.Comp.IntegralItem is { } item && !TerminatingOrDeleted(item)) QueueDel(item);
        module.Comp.Body = null;
        if (body is { } owner && !TerminatingOrDeleted(owner)) RefreshBody(owner);
    }
    private void OnBodyInit(Entity<NCCyberwareAbilityBodyComponent> body, ref MapInitEvent args) => RefreshBody(body);
    private void OnBodyChanged(Entity<NCCyberwareAbilityBodyComponent> body, ref NCMedicalBodyChangedEvent args) => RefreshBody(body);
    private void OnInstalled(Entity<NCCyberwareAbilityBodyComponent> body, ref NCCyberwareInstalledEvent args) => RefreshBody(body);
    private void OnRemoved(Entity<NCCyberwareAbilityBodyComponent> body, ref NCCyberwareRemovedEvent args) => RefreshBody(body);
    private void OnStateChanged(Entity<NCCyberwareAbilityBodyComponent> body, ref NCCyberwareStateChangedEvent args) => RefreshBody(body);
    private void OnFunctionsChanged(Entity<NCCyberwareAbilityBodyComponent> body, ref NCBodyFunctionChangedEvent args) => RefreshBody(body);
    private void OnBodyShutdown(Entity<NCCyberwareAbilityBodyComponent> body, ref ComponentShutdown args)
    {
        foreach (var uid in body.Comp.Modules.ToArray())
            if (TryComp<NCCyberwareAbilityComponent>(uid, out var module) && module.Body == body.Owner)
            { Stop((uid, module)); _actions.RemoveAction(module.Action); module.Body = null; }
        body.Comp.Modules.Clear();
    }

    private NCCyberwareAbilityPrototype GetProfile(NCCyberwareAbilityComponent module)
    {
        var profile = _prototypes.Index(module.Profile);
        if (!Enum.IsDefined(profile.Kind) || !float.IsFinite(profile.StartupEnergy) || profile.StartupEnergy < 0f ||
            !NCCyberwareAbilityMath.ValidCooldown(profile.CooldownSeconds) ||
            profile.Kind == NCCyberwareAbilityKind.MotorBurst && profile.CooldownSeconds + profile.DurationSeconds > 86400f ||
            !float.IsFinite(profile.DurationSeconds) || profile.DurationSeconds is <= 0f or > 60f ||
            !float.IsFinite(profile.MotorBonus) || profile.MotorBonus is < 0f or > 1f ||
            !float.IsFinite(profile.ManualDurationFactor) || profile.ManualDurationFactor is <= 0f or > 1f ||
            !float.IsFinite(profile.MovingPenaltyReduction) || profile.MovingPenaltyReduction is < 0f or > 1f ||
            (profile.Kind is NCCyberwareAbilityKind.Tool or NCCyberwareAbilityKind.Blade) != profile.IntegralItem.HasValue)
            throw new InvalidOperationException($"Invalid cyberware ability {profile.ID}.");
        return profile;
    }

    public bool CanOperate(EntityUid body, EntityUid uid)
    {
        return !TerminatingOrDeleted(body) && !TerminatingOrDeleted(uid) &&
               _cyberware.TryGetOwningBody(uid, out var owner) && owner == body &&
               TryComp<NCCyberwareComponent>(uid, out var hardware) && hardware.Operational && !hardware.CrisisBlocksActiveModes &&
               _cyberware.FirmwareAllowsActiveModes(uid) &&
               TryComp<BodyComponent>(body, out var graph) && graph.Organs != null &&
               graph.Organs.ContainedEntities.Any(organ => HasComp<Content.Shared.Body.Components.BrainComponent>(organ)) &&
               TryComp<NCPhysiologyComponent>(body, out var physiology) && !physiology.Irreversible &&
               physiology.Consciousness < NCConsciousnessState.Stuporous;
    }

    public void RefreshBody(EntityUid body)
    {
        if (!TryComp<NCCyberwareAbilityBodyComponent>(body, out var controls) || TerminatingOrDeleted(body) ||
            !_refreshing.Add(body)) return;
        try
        {
            var current = _cyberware.CollectCyberware(body)
                .Where(uid => !TerminatingOrDeleted(uid) && HasComp<NCCyberwareAbilityComponent>(uid)).ToHashSet();
            foreach (var uid in controls.Modules.Except(current).ToArray())
            {
                if (!TryComp<NCCyberwareAbilityComponent>(uid, out var old) || old.Body != body) continue;
                Stop((uid, old));
                _actions.RemoveAction(old.Action);
                old.Body = null;
            }
            controls.Modules = current;
            foreach (var uid in current)
            {
                var module = Comp<NCCyberwareAbilityComponent>(uid);
                var profile = GetProfile(module);
                if (module.Body is { } previousBody && previousBody != body)
                {
                    Stop((uid, module));
                    _actions.RemoveAction(module.Action);
                }
                module.Body = body;
                if (!CanOperate(body, uid))
                { Stop((uid, module)); _actions.RemoveAction(module.Action); continue; }
                if (_actions.GetAction(module.Action) is not { } existingAction || existingAction.Comp.AttachedEntity != body)
                    _actions.AddAction(body, ref module.Action, profile.Action.Id, uid);
                _actions.SetToggled(module.Action, module.Active);
                if (!module.Active)
                {
                    var remaining = NCCyberwareAbilityMath.Remaining(module.CooldownRemainingSeconds, module.CooldownObservedAt, _timing.CurTime);
                    if (remaining > 0f && _actions.GetAction(module.Action) is { } action)
                    {
                        var end = _timing.CurTime + TimeSpan.FromSeconds(remaining);
                        if (action.Comp.Cooldown is not { } cooldown || Math.Abs((cooldown.End - end).TotalSeconds) > 0.01)
                            _actions.SetCooldown(module.Action,
                                end - TimeSpan.FromSeconds(profile.DurationSeconds + profile.CooldownSeconds), end);
                    }
                }
            }
            RefreshModifiers(body, controls);
        }
        finally { _refreshing.Remove(body); }
    }

    private void RefreshModifiers(EntityUid body, NCCyberwareAbilityBodyComponent controls)
    {
        var motor = 0f;
        var manual = 1f;
        var stabilization = 0f;
        var policy = _prototypes.Index(NCCyberwareAbilityModifiersSystem.PolicyId);
        if (!NCCyberwareAbilityMath.ValidatePolicy(policy))
            throw new InvalidOperationException("Invalid cyberware ability policy.");
        foreach (var uid in controls.Modules)
        {
            if (!TryComp<NCCyberwareAbilityComponent>(uid, out var module) || !module.Active || !CanOperate(body, uid)) continue;
            var profile = GetProfile(module);
            var hardware = Comp<NCCyberwareComponent>(uid);
            var efficiency = Math.Clamp(hardware.FunctionalEfficiency * hardware.CrisisEfficiency, 0f, 1f);
            if (profile.Kind == NCCyberwareAbilityKind.MotorBurst)
            {
                motor = MathF.Max(motor, MathF.Min(profile.MotorBonus, policy.MaximumMotorBonus) * efficiency);
                manual = MathF.Min(manual, 1f - (1f - MathF.Max(profile.ManualDurationFactor, policy.MinimumManualDurationFactor)) * efficiency);
            }
            else if (profile.Kind == NCCyberwareAbilityKind.Stabilization)
                stabilization = MathF.Max(stabilization, MathF.Min(profile.MovingPenaltyReduction, policy.MaximumPenaltyReduction) * efficiency);
        }
        if (motor > 0f) stabilization = 0f;
        var next = (motor, manual, stabilization, motor > 0f ? policy.MaximumWalkSpeed : 0f, motor > 0f ? policy.MaximumSprintSpeed : 0f);
        var previous = (controls.MotorBonus, controls.ManualDurationFactor, controls.MovingPenaltyReduction, controls.MaximumWalkSpeed, controls.MaximumSprintSpeed);
        if (next == previous) return;
        (controls.MotorBonus, controls.ManualDurationFactor, controls.MovingPenaltyReduction, controls.MaximumWalkSpeed, controls.MaximumSprintSpeed) = next;
        Dirty(body, controls);
        _movement.RefreshMovementSpeedModifiers(body);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var now = _timing.CurTime;
        if (_active.Count == 0 || now < _nextTick) return;
        _nextTick = now + TimeSpan.FromSeconds(0.1);
        var settled = new HashSet<EntityUid>();
        foreach (var uid in _active.ToArray())
        {
            if (!TryComp<NCCyberwareAbilityComponent>(uid, out var module) || module.Body is not { } body)
            { _active.Remove(uid); continue; }
            if (!module.Active) { _active.Remove(uid); continue; }
            if (settled.Add(body)) _cyberware.TryAdvanceBodyPower(body, now);
            if (!module.Active) { _active.Remove(uid); continue; }
            if (!CanOperate(body, uid) || module.ActiveUntil is { } deadline && now >= deadline)
            { Stop((uid, module)); RefreshBody(body); continue; }
            if (GetProfile(module).ActiveStress is { } stimulus && now >= module.NextStressAt)
            {
                _humanity.TryApplyStimulus(body, stimulus, Guid.NewGuid());
                module.NextStressAt = now + TimeSpan.FromSeconds(1);
            }
        }
    }
}

[RegisterComponent]
public sealed partial class NCIntegralCyberwareItemComponent : Component
{
    public EntityUid? Module;
}
