// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Anatomy.Detachment;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Anatomy.Detachment;

/// <summary>
/// Converts an already accepted local wound into a traumatic detachment only when the current impact and
/// the live limb structure cross every prototype-defined gate. This system performs no random roll.
/// </summary>
public sealed partial class NCTraumaticSeveranceSystem : EntitySystem
{
    private const int MaximumRememberedActionRegions = 32;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCAnatomySystem _anatomy = default!;
    [Dependency] private NCOrganDetachmentSystem _detachment = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCMedicalBodyComponent, NCSeveranceAssessmentEvent>(OnAssessment);
        SubscribeLocalEvent<NCTraumaticSeveranceAccumulatorComponent, ComponentInit>(OnAccumulatorInit);
    }

    private void OnAccumulatorInit(
        Entity<NCTraumaticSeveranceAccumulatorComponent> entity,
        ref ComponentInit args)
    {
        // Robust can recycle component instances after deletion. Runtime action correlation must not leak bodies.
        entity.Comp.Trauma = new Dictionary<NCTraumaticSeveranceAccumulatorKey, float>();
        entity.Comp.Order = new Queue<NCTraumaticSeveranceAccumulatorKey>();
    }

    private void OnAssessment(Entity<NCMedicalBodyComponent> entity, ref NCSeveranceAssessmentEvent args)
    {
        if (!TryComp<NCWoundContainerComponent>(entity, out var wounds) ||
            !_prototypes.TryIndex(wounds.Profile, out var woundProfile) ||
            !_prototypes.TryIndex(woundProfile.SeveranceProfile, out var severanceProfile) ||
            !severanceProfile.Mechanisms.TryGetValue(args.WoundType, out var mechanism) ||
            !severanceProfile.Regions.TryGetValue(args.Request.MedicalRegion, out var regionRule) ||
            !_anatomy.TryGetOrgan(entity, args.Request.MedicalRegion, out var region) ||
            !TryComp<NCOrganIntegrityComponent>(region, out var integrity) ||
            integrity.MaxIntegrity <= 0f ||
            !TryComp<ChildOrganComponent>(region, out var child) ||
            child.Parent is not { } parent ||
            !TryComp<OrganComponent>(parent, out var parentOrgan) ||
            parentOrgan.Category is not { } parentCategory)
        {
            return;
        }

        NCBoneState? boneState = null;
        if (integrity.Material != NCAnatomicalMaterialKind.Mechanical &&
            regionRule.BoneCategory is { } boneCategory)
        {
            if (!_anatomy.TryGetOrgan(entity, boneCategory, out var bone) ||
                !TryComp<NCBoneComponent>(bone, out var boneComponent))
            {
                return;
            }

            boneState = boneComponent.State;
        }

        float? tendonIntegrity = null;
        if (integrity.Material != NCAnatomicalMaterialKind.Mechanical &&
            regionRule.TendonCategory is { } tendonCategory)
        {
            if (!_anatomy.TryGetOrgan(entity, tendonCategory, out var tendon) ||
                !TryComp<NCTendonComponent>(tendon, out var tendonComponent))
            {
                return;
            }

            tendonIntegrity = tendonComponent.Integrity;
        }

        var integrityFraction = Math.Clamp(integrity.CurrentIntegrity / integrity.MaxIntegrity, 0f, 1f);
        var compoundActionTrauma = AccumulateActionTrauma(entity.Owner, args);
        if (!NCTraumaticSeveranceRules.CanSever(
                mechanism,
                args.Severity,
                args.AppliedTrauma,
                compoundActionTrauma,
                integrityFraction,
                integrity.Material,
                boneState,
                tendonIntegrity) ||
            !_detachment.TryDetachOrgan(entity, args.Request.MedicalRegion, out _))
        {
            return;
        }

        var stumpTrauma = regionRule.StumpTrauma;
        if (integrity.Material == NCAnatomicalMaterialKind.Mechanical)
            stumpTrauma *= regionRule.MechanicalStumpTraumaMultiplier;

        var amputated = new NCTraumaticAmputationEvent(
            args.Request,
            args.Request.MedicalRegion,
            parentCategory,
            regionRule.TargetZone,
            severanceProfile.StumpTraumaProfile,
            stumpTrauma,
            integrity.Material == NCAnatomicalMaterialKind.Mechanical);
        RaiseLocalEvent(entity.Owner, ref amputated);
    }

    /// <summary>
    /// Aggregates accepted contacts from one immutable action only within the same medical region. This makes a
    /// close shotgun volley physically meaningful without allowing separate handgun shots or neighbouring zones
    /// to pool damage. The bounded FIFO is event-driven and never scanned from Update().
    /// </summary>
    private float AccumulateActionTrauma(EntityUid body, in NCSeveranceAssessmentEvent args)
    {
        if (!args.Request.ResolutionId.IsValid ||
            !float.IsFinite(args.AppliedTrauma) ||
            args.AppliedTrauma <= 0f)
        {
            return MathF.Max(0f, args.AppliedTrauma);
        }

        var state = EnsureComp<NCTraumaticSeveranceAccumulatorComponent>(body);
        var key = new NCTraumaticSeveranceAccumulatorKey(
            args.Request.ResolutionId.Value,
            args.Request.MedicalRegion,
            args.WoundType);

        if (!state.Trauma.TryGetValue(key, out var accumulated))
        {
            while (state.Order.Count >= MaximumRememberedActionRegions)
                state.Trauma.Remove(state.Order.Dequeue());

            state.Order.Enqueue(key);
        }

        // Sum in double precision and saturate only at the representable float boundary; this is an overflow
        // guard, not a balance cap. Gameplay thresholds remain entirely prototype-driven.
        var total = Math.Min(float.MaxValue, (double) accumulated + args.AppliedTrauma);
        var result = (float) total;
        state.Trauma[key] = result;
        return result;
    }
}
