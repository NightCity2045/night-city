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
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCAnatomySystem _anatomy = default!;
    [Dependency] private NCOrganDetachmentSystem _detachment = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCMedicalBodyComponent, NCSeveranceAssessmentEvent>(OnAssessment);
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
        if (!NCTraumaticSeveranceRules.CanSever(
                mechanism,
                args.Severity,
                args.AppliedTrauma,
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
}
