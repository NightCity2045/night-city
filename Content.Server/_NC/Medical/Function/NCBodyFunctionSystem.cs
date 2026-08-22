// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Function;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.Body;
using Content.Shared.Eye.Blinding.Components;
using Content.Shared.Eye.Blinding.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Function;

/// <summary>
/// Reduces one authoritative human's live organ container to bounded gameplay functions.
/// It runs only after relevant local events and never scans entities outside that body.
/// </summary>
public sealed partial class NCBodyFunctionSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedNCBodyFunctionSystem _bodyFunctions = default!;
    [Dependency] private BlindableSystem _blindable = default!;
    [Dependency] private NCPharmacologySystem _pharmacology = default!;

    private readonly HashSet<ProtoId<NCBodyFunctionProfilePrototype>> _validatedProfiles = [];
    private readonly Dictionary<EntityUid, PhysiologyFunctionState> _physiologyStates = [];
    private long _refreshes;
    private long _suppressedPhysiologyRefreshes;

    public NCBodyFunctionRuntimeCounters GetRuntimeCounters() => new(
        _physiologyStates.Count,
        _refreshes,
        _suppressedPhysiologyRefreshes);

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCBodyFunctionComponent, MapInitEvent>(
            OnBodyMapInit,
            after: [typeof(InitialBodySystem)]);
        SubscribeLocalEvent<NCBodyFunctionComponent, NCMedicalBodyChangedEvent>(OnMedicalBodyChanged);
        SubscribeLocalEvent<NCMedicalBodyComponent, NCPhysiologyEvaluatedEvent>(OnPhysiologyEvaluated);
        SubscribeLocalEvent<NCMedicalBodyComponent, OrganInsertedIntoEvent>(OnOrganInserted);
        SubscribeLocalEvent<NCMedicalBodyComponent, OrganRemovedFromEvent>(OnOrganRemoved);
        SubscribeLocalEvent<NCBodyFunctionComponent, ComponentShutdown>(OnBodyShutdown);
    }

    private void OnBodyMapInit(Entity<NCBodyFunctionComponent> entity, ref MapInitEvent args)
    {
        RefreshBody(entity.Owner);
    }

    private void OnMedicalBodyChanged(Entity<NCBodyFunctionComponent> entity, ref NCMedicalBodyChangedEvent args)
    {
        const NCMedicalDirtyDomains structural = NCMedicalDirtyDomains.Anatomy |
                                                 NCMedicalDirtyDomains.OrganFunction;
        if ((args.Domains & structural) != 0)
        {
            RefreshBody(entity.Owner);
            return;
        }

        const NCMedicalDirtyDomains physiology = NCMedicalDirtyDomains.Pain |
                                                  NCMedicalDirtyDomains.Consciousness;
        if ((args.Domains & physiology) != 0)
            RefreshBodyForPhysiologyChange(entity.Owner);
    }

    private void OnPhysiologyEvaluated(Entity<NCMedicalBodyComponent> entity, ref NCPhysiologyEvaluatedEvent args)
    {
        // This event is emitted after pain and consciousness have reached their current authoritative values.
        RefreshBodyForPhysiologyChange(entity.Owner);
    }

    private void OnOrganInserted(Entity<NCMedicalBodyComponent> entity, ref OrganInsertedIntoEvent args)
    {
        // InitialBody emits one event per organ. Its ordered MapInit handler performs one final graph refresh.
        if (LifeStage(entity.Owner) < EntityLifeStage.MapInitialized)
            return;

        RefreshBody(entity.Owner);
    }

    private void OnOrganRemoved(Entity<NCMedicalBodyComponent> entity, ref OrganRemovedFromEvent args)
    {
        if (LifeStage(entity.Owner) < EntityLifeStage.MapInitialized)
            return;

        RefreshBody(entity.Owner);
    }

    private void OnBodyShutdown(Entity<NCBodyFunctionComponent> entity, ref ComponentShutdown args)
    {
        _physiologyStates.Remove(entity.Owner);
    }

    /// <summary>
    /// Ongoing bleeding evaluates physiology frequently, but motor/sensory function changes only when its discrete
    /// pain or consciousness band changes. Suppress the full organ-graph reduction while both bands are stable.
    /// </summary>
    private void RefreshBodyForPhysiologyChange(EntityUid body)
    {
        if (!TryGetPhysiologyFunctionState(body, out var current))
        {
            RefreshBody(body);
            return;
        }

        if (_physiologyStates.TryGetValue(body, out var previous) && previous == current)
        {
            _suppressedPhysiologyRefreshes++;
            return;
        }

        RefreshBody(body);
    }

    /// <summary>
    /// Explicit deterministic refresh hook for tests, surgery and restore boundaries.
    /// </summary>
    public bool RefreshBody(EntityUid body)
    {
        if (!TryComp<NCMedicalBodyComponent>(body, out _) ||
            !TryComp<NCBodyFunctionComponent>(body, out var aggregate) ||
            !TryComp<BodyComponent>(body, out var bodyComponent) ||
            bodyComponent.Organs == null ||
            !_prototypes.TryIndex(aggregate.Profile, out var profile))
        {
            return false;
        }

        if (_validatedProfiles.Add(aggregate.Profile))
            ValidateProfile(profile);

        _refreshes++;

        // All dictionaries are bounded by the organ count of this one body (34 for an unmodified human).
        var organsByCategory = new Dictionary<ProtoId<OrganCategoryPrototype>, EntityUid>();
        var structuralChildren = new Dictionary<EntityUid, List<EntityUid>>();
        var organs = bodyComponent.Organs.ContainedEntities;
        foreach (var organ in organs)
        {
            if (TryComp<OrganComponent>(organ, out var organComponent) &&
                organComponent.Category is { } category)
            {
                organsByCategory.TryAdd(category, organ);
            }

            if (!TryComp<ChildOrganComponent>(organ, out var child) || child.Parent is not { } parent)
                continue;

            if (!structuralChildren.TryGetValue(parent, out var children))
            {
                children = [];
                structuralChildren.Add(parent, children);
            }
            children.Add(organ);
        }

        var movement = 0f;
        var leftGrip = 0f;
        var rightGrip = 0f;
        foreach (var binding in profile.Regions)
        {
            if (!organsByCategory.TryGetValue(binding.Category, out var region) ||
                !TryComp<NCAnatomicalRegionComponent>(region, out var anatomy))
            {
                continue;
            }

            var movementContribution = binding.IncludeMovement
                ? MathF.Max(0f, anatomy.MovementContribution)
                : 0f;
            if (movementContribution > 0f)
            {
                movement += movementContribution *
                            EvaluateMotorRegion(
                                region,
                                NCBodyFunctionChannel.Movement,
                                binding.IncludeParentSupport,
                                structuralChildren,
                                profile);
            }

            var gripContribution = binding.IncludeGrip
                ? MathF.Max(0f, anatomy.GripContribution)
                : 0f;
            if (gripContribution <= 0f)
                continue;

            var grip = gripContribution *
                       EvaluateMotorRegion(
                           region,
                           NCBodyFunctionChannel.Grip,
                           binding.IncludeParentSupport,
                           structuralChildren,
                           profile);
            switch (binding.Side)
            {
                case NCBodyFunctionSide.Left:
                    leftGrip += grip;
                    break;
                case NCBodyFunctionSide.Right:
                    rightGrip += grip;
                    break;
            }
        }

        movement = ClampUnit(movement / profile.ExpectedMovementContribution);
        leftGrip = ClampUnit(leftGrip / profile.ExpectedLeftGripContribution);
        rightGrip = ClampUnit(rightGrip / profile.ExpectedRightGripContribution);

        var pain = NCPainLevel.None;
        var consciousness = NCConsciousnessState.Alert;
        if (TryComp<NCPhysiologyComponent>(body, out var physiology))
        {
            pain = physiology.PainLevel;
            consciousness = physiology.Consciousness;
            _physiologyStates[body] = new PhysiologyFunctionState(pain, consciousness);
        }
        else
            _physiologyStates.Remove(body);

        var painMultiplier = profile.PainMotorMultipliers[(int) pain];
        var motorConsciousness = profile.ConsciousnessMotorMultipliers[(int) consciousness];
        var sensoryConsciousness = profile.ConsciousnessSensoryMultipliers[(int) consciousness];
        var pharmacologicalMotorControl = _pharmacology.GetModifiers(body).MotorControlMultiplier;
        movement = ClampUnit(
            movement * painMultiplier * motorConsciousness * pharmacologicalMotorControl);
        leftGrip = ClampUnit(
            leftGrip * painMultiplier * motorConsciousness * pharmacologicalMotorControl);
        rightGrip = ClampUnit(
            rightGrip * painMultiplier * motorConsciousness * pharmacologicalMotorControl);

        var vision = ClampUnit(EvaluateOrganFunction(
            organs,
            NCOrganFunctionType.Vision,
            profile.ExpectedVisionCapacity) * sensoryConsciousness);
        var hearing = ClampUnit(EvaluateOrganFunction(
            organs,
            NCOrganFunctionType.Hearing,
            profile.ExpectedHearingCapacity) * sensoryConsciousness);
        var speech = ClampUnit(EvaluateOrganFunction(
            organs,
            NCOrganFunctionType.Speech,
            profile.ExpectedSpeechCapacity) * sensoryConsciousness);

        var changed = _bodyFunctions.SetAggregate(
            (body, aggregate),
            new NCBodyFunctionAggregate(movement, leftGrip, rightGrip, vision, hearing, speech));

        // Blindable keeps the standard SS14 visibility/overlay contract, while the NC eyes remain the authority.
        if (changed && TryComp<BlindableComponent>(body, out var blindable))
            _blindable.UpdateIsBlind((body, blindable));

        return true;
    }

    private float EvaluateMotorRegion(
        EntityUid region,
        NCBodyFunctionChannel channel,
        bool includeParentSupport,
        IReadOnlyDictionary<EntityUid, List<EntityUid>> structuralChildren,
        NCBodyFunctionProfilePrototype profile)
    {
        var result = EvaluateSupportedRegion(region, channel, structuralChildren, profile);
        if (!includeParentSupport ||
            !TryComp<ChildOrganComponent>(region, out var child) ||
            child.Parent is not { } parent)
        {
            return result;
        }

        return MathF.Min(
            result,
            EvaluateSupportedRegion(parent, channel, structuralChildren, profile));
    }

    private float EvaluateSupportedRegion(
        EntityUid region,
        NCBodyFunctionChannel channel,
        IReadOnlyDictionary<EntityUid, List<EntityUid>> structuralChildren,
        NCBodyFunctionProfilePrototype profile)
    {
        var result = EvaluateLocalMotor(region, channel, profile);
        if (!structuralChildren.TryGetValue(region, out var children))
            return result;

        foreach (var child in children)
        {
            // Limb subtrees also contain hands and internal organs. Only their explicit support structures
            // constrain the parent's output; separately weighted anatomical regions are evaluated on their own.
            if (!HasComp<NCBoneComponent>(child) && !HasComp<NCTendonComponent>(child))
                continue;

            result = MathF.Min(result, EvaluateLocalMotor(child, channel, profile));
        }

        return ClampUnit(result);
    }

    private float EvaluateLocalMotor(
        EntityUid organ,
        NCBodyFunctionChannel channel,
        NCBodyFunctionProfilePrototype profile)
    {
        var result = EvaluateIntegrity(organ);

        if (TryComp<NCBoneComponent>(organ, out var bone))
        {
            var stateMultiplier = profile.BoneStateMultipliers[(int) bone.State];
            result = MathF.Min(result, ClampUnit(bone.Stability) * stateMultiplier);
        }

        if (TryComp<NCTendonComponent>(organ, out var tendon))
        {
            var contribution = channel == NCBodyFunctionChannel.Grip
                ? tendon.GripContribution
                : tendon.MovementContribution;
            var tendonFunction = MathF.Min(ClampUnit(tendon.Integrity), ClampUnit(contribution));
            if (tendon.Ruptured)
                tendonFunction *= profile.RupturedTendonMultiplier;
            result = MathF.Min(result, tendonFunction);
        }

        if (TryComp<NCJointComponent>(organ, out var joint))
        {
            var jointFunction = MathF.Min(
                ClampUnit(joint.Stability),
                ClampUnit(1f - joint.MovementRestriction));
            if (joint.Dislocated)
                jointFunction *= profile.DislocatedJointMultiplier;
            result = MathF.Min(result, jointFunction);
        }

        if (TryComp<NCNerveComponent>(organ, out var nerve))
        {
            result = MathF.Min(result, MathF.Min(
                ClampUnit(nerve.NeuralIntegrity),
                ClampUnit(nerve.MotorFunction)));
        }

        if (TryComp<NCOrganFunctionComponent>(organ, out var function) &&
            function.FunctionType == (channel == NCBodyFunctionChannel.Grip
                ? NCOrganFunctionType.Grip
                : NCOrganFunctionType.Movement))
        {
            result = MathF.Min(result, EvaluateFunctionComponent(function));
        }

        return ClampUnit(result);
    }

    private float EvaluateOrganFunction(
        IReadOnlyList<EntityUid> organs,
        NCOrganFunctionType type,
        float expectedCapacity)
    {
        var capacity = 0f;
        foreach (var organ in organs)
        {
            if (!TryComp<NCOrganFunctionComponent>(organ, out var function) ||
                function.FunctionType != type ||
                function.BaseCapacity <= 0f)
            {
                continue;
            }

            capacity += function.BaseCapacity * MathF.Min(
                EvaluateFunctionComponent(function),
                EvaluateIntegrity(organ));
        }

        return ClampUnit(capacity / expectedCapacity);
    }

    private static float EvaluateFunctionComponent(NCOrganFunctionComponent function)
    {
        if (function.BaseCapacity <= 0f)
            return 0f;

        return MathF.Min(
            ClampUnit(function.CurrentCapacity / function.BaseCapacity),
            ClampUnit(1f - function.Necrosis));
    }

    private float EvaluateIntegrity(EntityUid organ)
    {
        var cyberwareFunction = TryComp<NCCyberwareComponent>(organ, out var cyberware)
            ? ClampUnit(cyberware.FunctionalEfficiency)
            : 1f;
        if (!TryComp<NCOrganIntegrityComponent>(organ, out var integrity) || integrity.MaxIntegrity <= 0f)
            return cyberwareFunction;

        return MathF.Min(cyberwareFunction, MathF.Min(
            ClampUnit(integrity.CurrentIntegrity / integrity.MaxIntegrity),
            MathF.Min(
                ClampUnit(integrity.FunctionalEfficiency),
                ClampUnit(1f - integrity.Necrosis))));
    }

    private static void ValidateProfile(NCBodyFunctionProfilePrototype profile)
    {
        var categoryCount = Enum.GetValues<NCBoneState>().Length;
        var painCount = Enum.GetValues<NCPainLevel>().Length;
        var consciousnessCount = Enum.GetValues<NCConsciousnessState>().Length;
        var uniqueCategories = new HashSet<ProtoId<OrganCategoryPrototype>>();
        if (profile.Regions.Count == 0 ||
            profile.Regions.Any(binding =>
                !uniqueCategories.Add(binding.Category) ||
                !binding.IncludeMovement && !binding.IncludeGrip ||
                binding.IncludeGrip && binding.Side == NCBodyFunctionSide.Center) ||
            !PositiveFinite(profile.ExpectedMovementContribution) ||
            !PositiveFinite(profile.ExpectedLeftGripContribution) ||
            !PositiveFinite(profile.ExpectedRightGripContribution) ||
            !PositiveFinite(profile.ExpectedVisionCapacity) ||
            !PositiveFinite(profile.ExpectedHearingCapacity) ||
            !PositiveFinite(profile.ExpectedSpeechCapacity) ||
            profile.BoneStateMultipliers.Count != categoryCount ||
            profile.PainMotorMultipliers.Count != painCount ||
            profile.ConsciousnessMotorMultipliers.Count != consciousnessCount ||
            profile.ConsciousnessSensoryMultipliers.Count != consciousnessCount ||
            !AllUnit(profile.BoneStateMultipliers) ||
            !AllUnit(profile.PainMotorMultipliers) ||
            !AllUnit(profile.ConsciousnessMotorMultipliers) ||
            !AllUnit(profile.ConsciousnessSensoryMultipliers) ||
            !Unit(profile.DislocatedJointMultiplier) ||
            !Unit(profile.RupturedTendonMultiplier) ||
            !Unit(profile.SprintFunctionThreshold) ||
            !Unit(profile.RestrictedSprintMultiplier) ||
            !Unit(profile.FunctionalGripThreshold) ||
            !Unit(profile.FunctionalVisionThreshold) ||
            !Unit(profile.FunctionalHearingThreshold) ||
            !Unit(profile.FunctionalSpeechThreshold))
        {
            throw new InvalidOperationException($"NC body-function profile {profile.ID} is invalid.");
        }
    }

    private static bool AllUnit(IReadOnlyList<float> values)
    {
        foreach (var value in values)
        {
            if (!Unit(value))
                return false;
        }
        return true;
    }

    private static bool PositiveFinite(float value)
    {
        return float.IsFinite(value) && value > 0f;
    }

    private static bool Unit(float value)
    {
        return float.IsFinite(value) && value is >= 0f and <= 1f;
    }

    private static float ClampUnit(float value)
    {
        return float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
    }

    private enum NCBodyFunctionChannel : byte
    {
        Movement,
        Grip,
    }

    private bool TryGetPhysiologyFunctionState(EntityUid body, out PhysiologyFunctionState state)
    {
        if (!TryComp<NCPhysiologyComponent>(body, out var physiology))
        {
            state = default;
            return false;
        }

        state = new PhysiologyFunctionState(physiology.PainLevel, physiology.Consciousness);
        return true;
    }

    private readonly record struct PhysiologyFunctionState(
        NCPainLevel Pain,
        NCConsciousnessState Consciousness);
}

public readonly record struct NCBodyFunctionRuntimeCounters(
    int TrackedPhysiologyStates,
    long Refreshes,
    long SuppressedPhysiologyRefreshes);
