// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using Content.Server._NC.Medical.Cyberware.Abilities;
using Content.Server._NC.Medical.Wounds;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Abilities;
using Content.Shared._NC.Medical.Cyberware.Neural;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared.ActionBlocker;
using Content.Shared.Interaction;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Content.Shared.PowerCell;
using Content.Shared.Standing;
using Content.Shared.Verbs;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Cyberware.Neural;

[RegisterComponent]
public sealed partial class NCNeuralWearerComponent : Component;

public sealed partial class NCNeuralHardwareSystem : EntitySystem
{
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    [Dependency] private NCCyberwareAbilitySystem _abilities = default!;
    [Dependency] private NCMedicalBodySystem _medical = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private StandingStateSystem _standing = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private PowerCellSystem _cells = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    private TimeSpan _nextUpdate;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCNeuralWearerComponent, GetVerbsEvent<AlternativeVerb>>(OnWearerVerbs);
    }

    public bool IsReady(EntityUid module) => TryComp<NCCyberwareComponent>(module, out var hardware) &&
        hardware.Operational && hardware.PowerGranted && !hardware.ThermalShutdown && !hardware.CrisisBlocksActiveModes &&
        _cyberware.FirmwareAllowsActiveModes(module) && _cyberware.TryGetOwningBody(module, out _);

    public bool TryGetController(EntityUid module, out EntityUid controller) =>
        _cyberware.TryGetMedicalPart(module, "NCNeuralControllerSlot", out controller) && HasComp<NCNeuralControllerComponent>(controller);

    /// <summary>Fraction of the thermal shutdown threshold at which the Sandevistan turns itself off.</summary>
    private const float SandevistanCutoffHeat = 0.85f;

    private bool ShouldCutOff(EntityUid module)
    {
        var hardware = Comp<NCCyberwareComponent>(module);
        var threshold = _prototypes.Index(hardware.Profile).HeatShutdownThreshold;
        return threshold > 0 && hardware.Heat >= threshold * SandevistanCutoffHeat;
    }

    public bool TrySetEnabled(EntityUid module, bool enabled)
    {
        if (!TryComp<NCNeuralHardwareComponent>(module, out var neural) ||
            !_cyberware.TryGetOwningBody(module, out var body) || enabled && !IsReady(module)) return false;
        if (neural.Kind == NCNeuralKind.Sandevistan)
            return _abilities.TrySetEnabled(body, module, enabled);
        if (neural.Kind is NCNeuralKind.Link or NCNeuralKind.ChipSocket) return enabled;
        neural.Enabled = enabled;
        _medical.TryMarkDirty(body, NCMedicalDirtyDomains.Persistence);
        return true;
    }

    public bool TryEvade(EntityUid target, EntityUid? source, Content.Shared._NC.ActionResolution.NCActionResolutionId resolution, uint sequence)
    {
        if (!HasComp<NCMedicalBodyComponent>(target) || !resolution.IsValid ||
            source is not { } attacker || attacker == target || !Exists(attacker) ||
            !_blocker.CanMove(target) || _standing.IsDown(target) ||
            !TryComp<MobStateComponent>(target, out var mob) || mob.CurrentState != MobState.Alive ||
            !_interaction.InRangeUnobstructed(target, attacker, 20) ||
            !TryComp<NCCyberwareAbilityBodyComponent>(target, out var controls) || controls.MotorBonus > 0) return false;
        foreach (var uid in controls.Modules)
        {
            if (!TryComp<NCNeuralHardwareComponent>(uid, out var neural) || neural.Kind != NCNeuralKind.Kerenzikov ||
                !neural.Enabled || !IsReady(uid) || !_abilities.CanOperate(target, uid)) continue;
            if (_timing.CurTime < neural.NextDodge) return false;
            // One attempt per second, including failed rolls. Pellets cannot reroll one reaction.
            neural.NextDodge = _timing.CurTime + TimeSpan.FromSeconds(1);
            var hardware = Comp<NCCyberwareComponent>(uid);
            var chance = NCNeuralMath.DodgeChance(_cyberware.QualityPerformance(uid), hardware.FunctionalEfficiency * hardware.CrisisEfficiency);
            var roll = new Random(unchecked((int) resolution.DeriveSeed(sequence ^ 0x4B455245))).NextDouble();
            if (roll >= chance || !_cyberware.TryBeginAbilityLoad(uid, 2)) return false;
            _cyberware.TrySetLoad(uid, 0);
            _popup.PopupEntity(Loc.GetString("nc-neural-dodged", ("user", target)), target, PopupType.Small);
            return true;
        }
        return false;
    }

    public override void Update(float frameTime)
    {
        if (_timing.CurTime < _nextUpdate) return;
        _nextUpdate = _timing.CurTime + TimeSpan.FromSeconds(.5);
        var activeBodies = new HashSet<EntityUid>();
        var query = EntityQueryEnumerator<NCNeuralHardwareComponent>();
        while (query.MoveNext(out var uid, out var neural))
        {
            if (IsPaused(uid) || !_cyberware.TryGetOwningBody(uid, out var body)) continue;
            EnsureComp<NCNeuralWearerComponent>(body);
            if (neural.Kind == NCNeuralKind.Sandevistan && TryComp<Robust.Shared.Player.ActorComponent>(body, out var actor))
            {
                var hardware = Comp<NCCyberwareComponent>(uid);
                var definition = _prototypes.Index(hardware.Profile);
                var hasState = _abilities.TryGetFunctionState(uid, out var state);
                RaiseNetworkEvent(new NCNeuralStatusEvent { Body = GetNetEntity(body),
                    Heat = definition.HeatShutdownThreshold > 0 ? hardware.Heat / definition.HeatShutdownThreshold : 0,
                    CooldownSeconds = hasState ? (int) Math.Ceiling(state.CooldownMs / 1000d) : 0,
                    Active = hasState && state.Active }, actor.PlayerSession.Channel);
            }
            if (neural.Kind != NCNeuralKind.Sandevistan || !TryComp<NCCyberwareAbilityComponent>(uid, out var ability) || !ability.Active || !IsReady(uid)) continue;
            if (ShouldCutOff(uid))
            {
                // Firmware cutoff ahead of the native thermal shutdown, so overheating stays a player choice to avoid.
                if (_abilities.TrySetEnabled(body, uid, false))
                    _popup.PopupEntity(Loc.GetString("nc-neural-sandevistan-cutoff"), body, body, PopupType.MediumCaution);
                continue;
            }
            activeBodies.Add(body);
            var visual = EnsureComp<NCNeuralVisualComponent>(body);
            if (!visual.Burst) { visual.Burst = true; Dirty(body, visual); }
        }
        var visuals = EntityQueryEnumerator<NCNeuralVisualComponent>();
        while (visuals.MoveNext(out var body, out var visual))
            if (visual.Burst && !activeBodies.Contains(body)) { visual.Burst = false; Dirty(body, visual); }
    }
}
