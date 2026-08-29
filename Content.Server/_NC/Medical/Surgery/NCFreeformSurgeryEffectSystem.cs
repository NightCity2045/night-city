// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using System.Linq;
using Content.Server._NC.Medical.Anatomy.Detachment;
using Content.Server._NC.Medical.Bleeding;
using Content.Server._NC.Medical.Cyberware;
using Content.Server._NC.Medical.Wounds;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Damage;
using Content.Shared.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Popups;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Surgery;

/// <summary>
/// Applies spatial tool contact to existing authoritative medical state. It owns no sessions or UI and can
/// therefore be reused by future robotic or accessibility adapters without duplicating physiology mutations.
/// </summary>
public sealed partial class NCFreeformSurgeryEffectSystem : EntitySystem
{
    private static readonly EntProtoId LegacyExtractedForeignBody = "NCExtractedBullet";

    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCWoundSystem _wounds = default!;
    [Dependency] private NCBleedingSystem _bleeding = default!;
    [Dependency] private NCCavityFluidSystem _cavityFluid = default!;
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;
    [Dependency] private NCOrganDetachmentSystem _detachment = default!;
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    [Dependency] private NCSurgerySystem _legacy = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    public NCFreeformSurgeryEffectResult ApplyStroke(in NCFreeformSurgeryEffectContext context)
    {
        if (!TryComp<NCSurgicalSiteComponent>(context.Region, out var site) ||
            !TryComp<NCMedicalBodyComponent>(context.Patient, out var medical) ||
            context.Points.Length < 2)
        {
            return NCFreeformSurgeryEffectResult.Rejected;
        }

        var length = PathLength(context.Points);
        var power = Math.Clamp(
            length * context.Definition.PowerPerUnit * MathF.Max(0f, context.ToolQuality),
            context.Definition.MinimumPower,
            context.Definition.MaximumPower);
        if (!float.IsFinite(power) || power <= 0f)
            return NCFreeformSurgeryEffectResult.Rejected;
        if (context.Depth < context.Definition.MinimumDepth || context.Depth > context.Definition.MaximumDepth)
            return NCFreeformSurgeryEffectResult.NoTarget;
        var center = StrokeCenter(context.Points);
        if (context.Definition.RequiresOpenAccess && !HasLocalOpenAccess(context, site, center))
            return NCFreeformSurgeryEffectResult.AccessClosed;
        if (!_legacy.CanConsumeToolResource(context.Tool, context.ToolComponent, context.Definition.ConsumedUses))
            return NCFreeformSurgeryEffectResult.ResourceEmpty;

        var resolution = NCActionResolutionId.New();
        var applied = context.Definition.Action switch
        {
            NCFreeformSurgeryAction.Disinfect => ApplyDisinfection(context, site, center, power),
            NCFreeformSurgeryAction.Incise => ApplyIncision(context, site, medical, resolution, power),
            NCFreeformSurgeryAction.Autopsy => ApplyIncision(context, site, medical, resolution, power),
            NCFreeformSurgeryAction.Hemostasis => ApplyHemostasis(context, site, center, power, false),
            NCFreeformSurgeryAction.Cauterize => ApplyHemostasis(context, site, center, power, true),
            NCFreeformSurgeryAction.Retract => ApplyRetraction(context, site, power),
            NCFreeformSurgeryAction.Suture => ApplySuture(context, site, center, power),
            NCFreeformSurgeryAction.Irrigate => ApplyIrrigation(context, site, center, power),
            NCFreeformSurgeryAction.Debride => ApplyDebridement(context, site, center, power),
            NCFreeformSurgeryAction.RepairVessel => ApplyVascularRepair(context, center, power),
            NCFreeformSurgeryAction.RepairOrgan => ApplyOrganRepair(context, power),
            NCFreeformSurgeryAction.ReduceBone => ApplyBone(context, site, reduce: true),
            NCFreeformSurgeryAction.FixBone => ApplyBone(context, site, reduce: false),
            NCFreeformSurgeryAction.RepairTendon => ApplyTendon(context, power),
            NCFreeformSurgeryAction.RepairNerve => ApplyNerve(context, power),
            NCFreeformSurgeryAction.ExtractForeignBody => ApplyExtraction(context, center, resolution),
            NCFreeformSurgeryAction.OpenBone => ApplyBoneAccess(context, site),
            NCFreeformSurgeryAction.Separate => ApplySeparation(context),
            NCFreeformSurgeryAction.Amputate => ApplyAmputation(context),
            NCFreeformSurgeryAction.Transplant => ApplyTransplant(context),
            NCFreeformSurgeryAction.Cyberware => ApplyCyberware(context, resolution),
            NCFreeformSurgeryAction.DrainCavity => ApplyCavityDrain(context, power),
            _ => false,
        };

        // NC: Physical danger belongs to the held item as well as the chosen capability. A laser scalpel in
        // debridement mode still cuts live anatomy when no contaminated wound exists under the cursor.
        var physicalDamage = MathF.Max(
            context.Definition.MisuseStructureDamagePerPower,
            context.ToolComponent.PhysicalContactDamagePerPower);
        if (!applied && physicalDamage > 0f)
        {
            applied = ApplyStructureDamage(
                context,
                power,
                physicalDamage,
                true);
        }

        if (!applied)
            return NCFreeformSurgeryEffectResult.NoTarget;

        // Dirt never blocks a physical intervention. It is transferred into the real local site and wounds.
        var introduced = MathF.Max(0f, context.Definition.ContaminationPerUnit) * length;
        introduced += (1f - Math.Clamp(context.ToolSterility, 0f, 1f)) * context.UnpreparedContamination;
        site.Contamination = Math.Clamp(site.Contamination + introduced, 0f, 1f);
        unchecked
        {
            site.SpatialRevision++;
        }

        _legacy.TryConsumeToolResource(context.Tool, context.ToolComponent, context.Definition.ConsumedUses);

        _medicalBodies.TryMarkDirty(
            (context.Patient, medical),
            NCMedicalDirtyDomains.Wounds |
            NCMedicalDirtyDomains.Vascular |
            NCMedicalDirtyDomains.Pain |
            NCMedicalDirtyDomains.OrganFunction |
            NCMedicalDirtyDomains.Consciousness |
            NCMedicalDirtyDomains.Diagnostics |
            NCMedicalDirtyDomains.Persistence);
        return new NCFreeformSurgeryEffectResult(true, true, null);
    }

    private bool ApplyIncision(
        in NCFreeformSurgeryEffectContext context,
        NCSurgicalSiteComponent site,
        NCMedicalBodyComponent medical,
        NCActionResolutionId resolution,
        float power)
    {
        if (context.Definition.TraumaProfile is not { } traumaProfile)
            return false;

        var direction = context.Points[^1] - context.Points[0];
        if (direction.IsLengthZero())
            direction = Vector2.UnitY;
        var center = StrokeCenter(context.Points);
        var request = new NCCombatTraumaRequest(
            resolution,
            1,
            1,
            context.Patient,
            context.Surgeon,
            context.Tool,
            null,
            context.Zone,
            Comp<OrganComponent>(context.Region).Category ?? default,
            traumaProfile,
            new DamageSpecifier(),
            FixedPoint2.New(power),
            FixedPoint2.Zero,
            FixedPoint2.New(power),
            FixedPoint2.Zero,
            center - new Vector2(0.5f, 0.5f),
            SurfaceDirection(context.Surface, direction),
            NCTraumaFlags.Penetrating,
            Math.Clamp(site.Contamination + (1f - context.ToolSterility), 0f, 1f),
            _timing.CurTime);
        var result = _wounds.ApplyTrauma(context.Patient, request);
        if (!result.Accepted)
            return false;

        var tissueDepth = ResolveTissueDepth(context.Region, context.Depth, out var access);
        if (result.WoundId is { } woundId &&
            TryComp<NCWoundContainerComponent>(context.Patient, out var wounds) &&
            wounds.Wounds.TryGetValue(woundId, out var wound))
        {
            wound.TissueDepth = tissueDepth;
            if (tissueDepth >= NCTissueDepth.Cavity && context.Definition.InternalBleedPerPowerAtCavity > 0f)
            {
                var internalRate = power * context.Definition.InternalBleedPerPowerAtCavity;
                wound.InternalBleedRate += internalRate;
                wound.InitialInternalBleedRate += internalRate;
                wound.PrimaryBleedKind = NCBleedKind.Internal;
                _bleeding.SynchronizeWoundSources(context.Patient, wound);
            }
        }

        // NC: A body zone and surface own exactly one operative site. Repeated cutting deepens or reopens that
        // same site; cursor position, patient movement and an earlier closure can never create a second aperture.
        var existing = FindCanonicalIncision(site, context.Zone, context.Surface);
        if (existing != null)
        {
            existing.Depth = MathF.Max(existing.Depth, context.Depth);
            existing.Width = MathF.Max(existing.Width, MathF.Max(0.01f, power * 0.004f));
            existing.Contamination = Math.Clamp(existing.Contamination + site.Contamination * 0.1f, 0f, 1f);
            existing.ModifiedAt = _timing.CurTime;
            existing.Polyline = Simplify(context.Points, 32);
            var wasSutured = existing.Sutured;
            existing.Sutured = false;
            if (wasSutured)
                existing.EdgeSeparation = 0f;
            LinkWound(existing, result.WoundId);
        }
        else
        {
            site.Incisions.Add(new NCSurgicalIncisionRecord
            {
                IncisionId = resolution.DeriveGuid(0x494E4349U),
                WoundId = result.WoundId,
                TargetZone = context.Zone,
                Surface = context.Surface,
                Polyline = Simplify(context.Points, 32),
                Depth = context.Depth,
                Width = MathF.Max(0.01f, power * 0.004f),
                Contamination = site.Contamination,
                SourceResolutionId = resolution,
                CreatedAt = _timing.CurTime,
                ModifiedAt = _timing.CurTime,
            });
            // NC: Operative traces persist for the round, but their identity is the canonical zone/surface slot.
        }

        if (access > site.AccessDepth)
            site.AccessDepth = access;
        ApplyIncidentalStructureDamage(context, power);
        return true;
    }

    private void ApplyIncidentalStructureDamage(in NCFreeformSurgeryEffectContext context, float power)
    {
        ApplyStructureDamage(context, power, context.Definition.IncidentalStructureDamagePerPower);
    }

    private bool ApplyStructureDamage(
        in NCFreeformSurgeryEffectContext context,
        float power,
        float damagePerPower,
        bool allowRegionFallback = false)
    {
        var target = context.Structure ?? (allowRegionFallback ? context.Region : null);
        if (target is not { } affectedTarget ||
            !allowRegionFallback && affectedTarget == context.Region ||
            damagePerPower <= 0f)
        {
            return false;
        }

        var damage = power * damagePerPower;
        var fraction = Math.Clamp(damage / 100f, 0f, 1f);
        var affected = false;
        if (TryComp<NCOrganIntegrityComponent>(affectedTarget, out var integrity))
        {
            integrity.CurrentIntegrity = MathF.Max(0f, integrity.CurrentIntegrity - damage);
            integrity.FunctionalEfficiency = MathF.Min(
                integrity.FunctionalEfficiency,
                integrity.CurrentIntegrity / MathF.Max(1f, integrity.MaxIntegrity));
            affected = true;
        }
        if (TryComp<NCOrganFunctionComponent>(affectedTarget, out var function))
        {
            function.CurrentCapacity = MathF.Max(0f, function.CurrentCapacity - fraction * function.BaseCapacity);
            affected = true;
        }
        if (TryComp<NCTendonComponent>(affectedTarget, out var tendon))
        {
            tendon.Integrity = MathF.Max(0f, tendon.Integrity - fraction);
            tendon.Ruptured |= tendon.Integrity < context.IncidentalTendonRuptureThreshold;
            affected = true;
        }
        if (TryComp<NCNerveComponent>(affectedTarget, out var nerve))
        {
            nerve.NeuralIntegrity = MathF.Max(0f, nerve.NeuralIntegrity - fraction);
            nerve.MotorFunction = MathF.Min(nerve.MotorFunction, nerve.NeuralIntegrity);
            affected = true;
        }
        if (TryComp<NCBoneComponent>(affectedTarget, out var bone))
        {
            bone.Stability = MathF.Max(0f, bone.Stability - fraction);
            if (bone.Stability < context.IncidentalBoneBreakThreshold)
                bone.State = NCBoneState.BrokenStable;
            else if (bone.Stability < context.IncidentalBoneCrackThreshold && bone.State < NCBoneState.Cracked)
                bone.State = NCBoneState.Cracked;
            affected = true;
        }
        return affected;
    }

    private bool ApplyCavityDrain(in NCFreeformSurgeryEffectContext context, float power)
    {
        var target = HasComp<NCCavityFluidComponent>(context.Region)
            ? context.Region
            : context.Structure;
        if (target is not { } cavity || !HasComp<NCCavityFluidComponent>(cavity))
            return false;

        var amount = MathF.Max(0f, context.Definition.ImmediateDrainVolumePerPower) * power;
        return amount > 0f && _cavityFluid.TryAdjustVolume(context.Patient, cavity, -amount);
    }

    private bool ApplyDisinfection(
        in NCFreeformSurgeryEffectContext context,
        NCSurgicalSiteComponent site,
        Vector2 point,
        float power)
    {
        // Field preparation is a real local mutation. It never grants permission to operate; it only lowers
        // the contamination that subsequent contacts transfer into this exact site and nearby wound.
        var fraction = Math.Clamp(power, 0f, 1f);
        site.Contamination *= 1f - fraction;
        if (TryNearestWound(context, point, out var wound))
        {
            wound.Contamination *= 1f - fraction;
            wound.ModifiedAt = _timing.CurTime;
        }

        return true;
    }

    private bool ApplyHemostasis(
        in NCFreeformSurgeryEffectContext context,
        NCSurgicalSiteComponent site,
        Vector2 point,
        float power,
        bool cauterize)
    {
        if (!TryNearestWound(context, point, out var wound))
            return false;

        var fraction = Math.Clamp(power, 0f, 1f);
        wound.ExternalBleedRate *= 1f - fraction;
        if (context.Depth >= 0.45f)
            wound.InternalBleedRate *= 1f - fraction * (cauterize ? 0.65f : 0.4f);
        wound.TreatmentFlags |= cauterize ? NCWoundTreatmentFlags.Cauterized : NCWoundTreatmentFlags.Clamped;
        if (cauterize)
        {
            wound.Necrosis = Math.Clamp(wound.Necrosis + fraction * 0.08f, 0f, 1f);
            wound.Pain += 4f * fraction;
        }
        site.BleedingControlled = wound.ExternalBleedRate <= 0.01f && wound.InternalBleedRate <= 0.01f;
        wound.ModifiedAt = _timing.CurTime;
        _bleeding.SynchronizeWoundSources(context.Patient, wound);
        return true;
    }

    private bool ApplyRetraction(
        in NCFreeformSurgeryEffectContext context,
        NCSurgicalSiteComponent site,
        float power)
    {
        var incision = FindOperativeSite(site, context.Zone, context.Surface);
        if (incision == null || incision.Sutured)
            return false;

        // NC: Retraction is a discrete interactive transition. A successful click must cross the same
        // data-driven threshold used by the authoritative renderer; otherwise prediction briefly opens the
        // aperture and the next server state closes it again despite the accepted tool action.
        incision.EdgeSeparation = Math.Clamp(
            MathF.Max(MathF.Max(incision.EdgeSeparation, power), context.MinimumOpenRetraction),
            0f,
            1f);
        incision.ModifiedAt = _timing.CurTime;
        site.TissuesRetracted = incision.EdgeSeparation >= context.MinimumOpenRetraction;
        if (site.TissuesRetracted && site.AccessDepth < NCSurgicalAccessDepth.SoftTissueRetracted)
            site.AccessDepth = NCSurgicalAccessDepth.SoftTissueRetracted;
        return true;
    }

    private bool ApplySuture(
        in NCFreeformSurgeryEffectContext context,
        NCSurgicalSiteComponent site,
        Vector2 point,
        float power)
    {
        var incision = FindOperativeSite(site, context.Zone, context.Surface);
        var hasWound = TryNearestWound(context, point, out var nearestWound);
        var incisionDistance = incision == null
            ? float.PositiveInfinity
            : Vector2.DistanceSquared(point, StrokeCenter(incision.Polyline));
        var woundDistance = hasWound
            ? Vector2.DistanceSquared(point, new Vector2(nearestWound.VisualX / 255f, nearestWound.VisualY / 255f))
            : float.PositiveInfinity;

        // NC: A wound sprite and an operative incision are independent interactive targets. The closest one
        // wins, so suturing a combat cut never silently closes the zone's surgical aperture instead.
        var woundBelongsToIncision = incision != null && EnumerateWoundIds(incision).Contains(nearestWound?.WoundId ?? Guid.Empty);
        if (hasWound && nearestWound != null && !woundBelongsToIncision && woundDistance < incisionDistance)
        {
            if (nearestWound.ClosureState is NCWoundClosureState.Healed or NCWoundClosureState.Sealed)
                return false;
            nearestWound.ExternalBleedRate = 0f;
            nearestWound.ClosureState = NCWoundClosureState.Sutured;
            nearestWound.Stage = NCWoundStage.Treated;
            nearestWound.ModifiedAt = _timing.CurTime;
            // Contamination, infection and necrosis deliberately remain: closing a dirty wound traps the
            // clinical problem instead of making it disappear.
            _bleeding.SynchronizeWoundSources(context.Patient, nearestWound);
            return true;
        }

        // The skin cannot be closed over a surgically opened skull/rib/pelvic casing. Bone fixation closes
        // that local casing first; ordinary external wounds remain independently suturable above.
        if (incision == null || incision.BoneCasingOpen)
            return false;

        incision.Width = MathF.Max(0f, incision.Width - power * 0.08f);
        incision.EdgeSeparation = MathF.Max(0f, incision.EdgeSeparation - power);
        incision.Sutured = incision.Width <= 0.012f || power >= 0.45f;
        incision.ModifiedAt = _timing.CurTime;
        if (TryComp<NCWoundContainerComponent>(context.Patient, out var wounds))
        {
            foreach (var woundId in EnumerateWoundIds(incision))
            {
                if (!wounds.Wounds.TryGetValue(woundId, out var wound))
                    continue;
                wound.ExternalBleedRate = 0f;
                wound.ClosureState = NCWoundClosureState.Sutured;
                wound.Stage = NCWoundStage.Treated;
                wound.ModifiedAt = _timing.CurTime;
                _bleeding.SynchronizeWoundSources(context.Patient, wound);
            }
        }

        RecalculateAccess(site, context.MinimumOpenRetraction);
        return true;
    }

    private bool ApplyIrrigation(
        in NCFreeformSurgeryEffectContext context,
        NCSurgicalSiteComponent site,
        Vector2 point,
        float power)
    {
        var fraction = Math.Clamp(power, 0f, 1f);
        site.Contamination *= 1f - fraction;
        if (TryNearestWound(context, point, out var wound))
        {
            wound.Contamination *= 1f - fraction;
            wound.TreatmentFlags |= NCWoundTreatmentFlags.Drained;
            wound.ModifiedAt = _timing.CurTime;
        }
        return true;
    }

    private bool ApplyDebridement(
        in NCFreeformSurgeryEffectContext context,
        NCSurgicalSiteComponent site,
        Vector2 point,
        float power)
    {
        if (!TryNearestWound(context, point, out var wound))
            return false;

        var fraction = Math.Clamp(power, 0f, 1f);
        wound.Contamination *= 1f - fraction;
        wound.Infection *= 1f - fraction * 0.7f;
        wound.Necrosis *= 1f - fraction * 0.65f;
        wound.TreatmentFlags |= NCWoundTreatmentFlags.Debrided;
        wound.ModifiedAt = _timing.CurTime;
        site.Contamination *= 1f - fraction * 0.5f;
        return true;
    }

    private bool ApplyVascularRepair(in NCFreeformSurgeryEffectContext context, Vector2 point, float power)
    {
        if (!TryNearestWound(context, point, out var wound))
            return false;

        var fraction = Math.Clamp(power, 0f, 1f);
        wound.ExternalBleedRate *= 1f - fraction;
        wound.InternalBleedRate *= 1f - fraction;
        wound.TreatmentFlags |= NCWoundTreatmentFlags.Clamped;
        wound.ModifiedAt = _timing.CurTime;
        _bleeding.SynchronizeWoundSources(context.Patient, wound);
        return true;
    }

    private bool ApplyOrganRepair(in NCFreeformSurgeryEffectContext context, float power)
    {
        var target = context.Structure ?? context.Region;
        if (!TryComp<NCOrganIntegrityComponent>(target, out var integrity))
            return false;

        integrity.CurrentIntegrity = Math.Clamp(
            integrity.CurrentIntegrity + Math.Clamp(power, 0f, 1f) * integrity.MaxIntegrity,
            0f,
            integrity.MaxIntegrity);
        integrity.Necrosis = MathF.Max(0f, integrity.Necrosis - power * 0.25f);
        if (TryComp<NCOrganFunctionComponent>(target, out var function))
        {
            function.CurrentCapacity = Math.Clamp(
                MathF.Max(function.CurrentCapacity, integrity.CurrentIntegrity / MathF.Max(1f, integrity.MaxIntegrity)),
                0f,
                function.BaseCapacity);
        }
        return true;
    }

    private bool ApplyBone(
        in NCFreeformSurgeryEffectContext context,
        NCSurgicalSiteComponent site,
        bool reduce)
    {
        var target = context.Structure ?? context.Region;
        if (!TryComp<NCBoneComponent>(target, out var bone))
            return false;

        if (reduce)
        {
            bone.Displacement = 0f;
            bone.State = bone.State > NCBoneState.BrokenStable ? NCBoneState.BrokenStable : bone.State;
            bone.Stability = MathF.Max(bone.Stability, 0.45f);
        }
        else
        {
            bone.RequiresFixation = false;
            bone.Stability = MathF.Max(bone.Stability, 0.8f);
            if (bone.State is NCBoneState.BrokenStable or NCBoneState.BrokenDisplaced)
                bone.State = NCBoneState.Cracked;
            if (context.StructureBlocksDeeperStructures &&
                FindOperativeSite(site, context.Zone, context.Surface) is { } incision)
            {
                incision.BoneCasingOpen = false;
                incision.ModifiedAt = _timing.CurTime;
                site.BoneCasingOpen = site.Incisions.Any(value => !value.Sutured && value.BoneCasingOpen);
            }
        }
        return true;
    }

    private bool ApplyTendon(in NCFreeformSurgeryEffectContext context, float power)
    {
        var target = context.Structure ?? context.Region;
        if (!TryComp<NCTendonComponent>(target, out var tendon))
            return false;
        tendon.Integrity = Math.Clamp(tendon.Integrity + power, 0f, 1f);
        tendon.Ruptured = tendon.Integrity < 0.45f;
        return true;
    }

    private bool ApplyNerve(in NCFreeformSurgeryEffectContext context, float power)
    {
        var target = context.Structure ?? context.Region;
        if (!TryComp<NCNerveComponent>(target, out var nerve))
            return false;
        nerve.NeuralIntegrity = Math.Clamp(nerve.NeuralIntegrity + power * 0.55f, 0f, 1f);
        nerve.MotorFunction = Math.Clamp(nerve.MotorFunction + power * 0.4f, 0f, 1f);
        return true;
    }

    private bool ApplyExtraction(
        in NCFreeformSurgeryEffectContext context,
        Vector2 point,
        NCActionResolutionId resolution)
    {
        if (!TryNearestWound(context, point, out var wound) || wound.EmbeddedObjectId is not { } foreignBodyId)
            return false;

        var prototype = wound.EmbeddedObjectEntityPrototype ?? LegacyExtractedForeignBody;
        if (!_prototypes.HasIndex<EntityPrototype>(prototype))
            return false;

        var extracted = Spawn(prototype, Transform(context.Patient).Coordinates);
        var forensic = EnsureComp<NCExtractedForeignBodyComponent>(extracted);
        forensic.ForeignBodyId = foreignBodyId;
        forensic.SourceProjectilePrototype = wound.SourceProjectilePrototype;
        wound.EmbeddedObjectId = null;
        wound.EmbeddedObjectEntityPrototype = null;
        wound.ModifiedAt = _timing.CurTime;
        if (!_hands.TryPickupAnyHand(context.Surgeon, extracted))
        {
            _popup.PopupEntity(
                Loc.GetString("nc-medical-freeform-surgery-extracted-foreign-body-placed"),
                extracted,
                context.Surgeon,
                PopupType.SmallCaution);
        }
        return true;
    }

    private bool ApplyBoneAccess(
        in NCFreeformSurgeryEffectContext context,
        NCSurgicalSiteComponent site)
    {
        if (!context.HasBoneTarget && !context.IsCranialRegion)
            return false;
        // NC: Capability quality and tool power were already validated by ApplyStroke. An accepted discrete
        // bone-access click therefore performs the state transition instead of succeeding below a hidden cutoff.
        var incision = FindOperativeSite(site, context.Zone, context.Surface);
        if (incision == null || incision.EdgeSeparation < context.MinimumOpenRetraction)
            return false;
        incision.BoneCasingOpen = true;
        incision.ModifiedAt = _timing.CurTime;
        // Compatibility summary for retained procedure adapters; freeform access always reads the local record.
        site.BoneCasingOpen = site.Incisions.Any(value => !value.Sutured && value.BoneCasingOpen);
        if (site.AccessDepth < NCSurgicalAccessDepth.EncasedStructureOpened)
            site.AccessDepth = NCSurgicalAccessDepth.EncasedStructureOpened;
        return true;
    }

    private bool ApplySeparation(in NCFreeformSurgeryEffectContext context)
    {
        // Organ separation is cursor-specific. Falling back to the selected external region would turn a
        // missed organ click into an accidental limb amputation.
        if (!context.StructureDetachableBySeparation || context.Structure is not { } target)
            return false;
        if (!TryComp<OrganComponent>(target, out var organ) || organ.Category is not { } category ||
            !_detachment.TryDetachOrgan(context.Patient, category, out var detached))
        {
            return false;
        }

        if (!_hands.TryPickupAnyHand(context.Surgeon, detached))
        {
            // NC: Detachment already placed the live entity at the patient's coordinates. Never hide a
            // successful extraction merely because the surgeon kept every hand occupied.
            _popup.PopupEntity(
                Loc.GetString("nc-medical-freeform-surgery-extracted-organ-placed"),
                detached,
                context.Surgeon,
                PopupType.SmallCaution);
        }
        return true;
    }

    private bool ApplyAmputation(in NCFreeformSurgeryEffectContext context)
    {
        // Amputation deliberately targets the selected anatomical region, not the deepest atlas structure
        // under the stroke. The prototype allowlist prevents an amputation tool from detaching a cavity.
        if (!TryComp<OrganComponent>(context.Region, out var organ) || organ.Category is not { } category ||
            context.Definition.AllowedRegionCategories.Count > 0 &&
            !context.Definition.AllowedRegionCategories.Contains(category) ||
            !_detachment.TryDetachOrgan(context.Patient, category, out var detached))
        {
            return false;
        }

        _hands.TryPickupAnyHand(context.Surgeon, detached);
        return true;
    }

    private bool ApplyTransplant(in NCFreeformSurgeryEffectContext context)
    {
        if (!TryComp<NCSurgeryGameplaySelectionComponent>(context.Surgeon, out var selection) ||
            selection.Patient != context.Patient || selection.Graft is not { } graft ||
            !TryComp<OrganComponent>(context.Region, out var parent) || parent.Category is not { } parentCategory)
        {
            return false;
        }

        if (!_detachment.TryAttachOrgan(context.Patient, parentCategory, graft))
            return false;

        RemComp<NCSurgeryGameplaySelectionComponent>(context.Surgeon);
        return true;
    }

    private bool ApplyCyberware(
        in NCFreeformSurgeryEffectContext context,
        NCActionResolutionId resolution)
    {
        if (TryComp<NCSurgeryGameplaySelectionComponent>(context.Surgeon, out var selection) &&
            selection.Patient == context.Patient && selection.Graft is { } module &&
            HasComp<NCCyberwareComponent>(module) &&
            _cyberware.TryFindInstallSlot(context.Patient, context.Region, module, out var slot))
        {
            var result = _cyberware.TryInstall(new NCCyberwareInstallRequest(
                resolution,
                context.Patient,
                context.Region,
                module,
                slot,
                ConnectNeuralInterface: true,
                CalibrateNeuralInterface: true));
            if (!result.Accepted)
                return false;

            RemComp<NCSurgeryGameplaySelectionComponent>(context.Surgeon);
            return true;
        }

        var target = context.Structure;
        if (target == null || !TryComp<NCCyberwareComponent>(target.Value, out _))
            return false;

        // With no staged module, contact on an exposed installed module is removal. General cyberware service
        // remains the responsibility of its physical service items and must not silently double as surgery.
        var removal = _cyberware.TryRemove(new NCCyberwareRemoveRequest(
            resolution,
            context.Patient,
            target.Value));
        if (!removal.Accepted)
            return false;

        _hands.TryPickupAnyHand(context.Surgeon, target.Value);
        return true;
    }

    private bool HasLocalOpenAccess(
        in NCFreeformSurgeryEffectContext context,
        NCSurgicalSiteComponent site,
        Vector2 point)
    {
        // A missing selected region is already an exposed receiving stump. The fallback parent exists only
        // as the authoritative attachment anchor and must not force an unrelated incision into that parent.
        if (context.Definition.Action == NCFreeformSurgeryAction.Transplant &&
            _prototypes.TryIndex(context.Zone, out NCTargetZonePrototype? zone) &&
            TryComp<OrganComponent>(context.Region, out var region) &&
            region.Category != zone.MedicalRegionCategory)
        {
            return true;
        }

        // NC: Retraction turns the zone into one discrete operative window. Once that window reaches the
        // requested layer, every visible sprite inside it is physically accessible without drawing another cut.
        var requiredDepth = context.StructureMinimumDepth ?? context.Depth;
        foreach (var incision in site.Incisions)
        {
            if (incision.TargetZone == context.Zone && incision.Surface == context.Surface &&
                !incision.Sutured && incision.EdgeSeparation >= context.MinimumOpenRetraction &&
                (incision.BoneCasingOpen || incision.Depth + 0.001f >= requiredDepth))
            {
                return true;
            }
        }

        // A pre-existing open penetrating wound is also a physical path. Dressings and completed closure do
        // not count, and the wound must expose at least the tissue layer selected by the instrument.
        if (!TryNearestWound(context, point, out var wound) || wound.ClosureState != NCWoundClosureState.Open)
            return false;
        var requiredTissue = ResolveTissueDepth(context.Region, context.Depth, out _);
        return wound.TissueDepth >= requiredTissue;
    }

    private bool TryNearestWound(
        in NCFreeformSurgeryEffectContext context,
        Vector2 point,
        out NCWoundRecord wound)
    {
        wound = default!;
        if (!TryComp<NCWoundContainerComponent>(context.Patient, out var wounds))
            return false;

        // NC: A visible wound/foreign-body sprite supplies its stable server-issued id. Resolve that record
        // directly so overlapping wounds can never make the nearest-point heuristic operate on another injury.
        if (context.WoundId is { } woundId)
        {
            if (!wounds.Wounds.TryGetValue(woundId, out var targeted) ||
                targeted.TargetZone != context.Zone || targeted.Surface != context.Surface)
            {
                return false;
            }

            wound = targeted;
            return true;
        }

        var bestDistance = context.Definition.Radius * context.Definition.Radius;
        foreach (var candidate in wounds.Wounds.Values)
        {
            if (candidate.TargetZone != context.Zone || candidate.Surface != context.Surface)
                continue;
            var candidatePoint = new Vector2(candidate.VisualX / 255f, candidate.VisualY / 255f);
            var distance = Vector2.DistanceSquared(point, candidatePoint);
            if (distance > bestDistance)
                continue;
            bestDistance = distance;
            wound = candidate;
        }
        return wound != null;
    }

    private NCTissueDepth ResolveTissueDepth(
        EntityUid region,
        float depth,
        out NCSurgicalAccessDepth access)
    {
        access = NCSurgicalAccessDepth.Closed;
        if (!TryComp<NCAnatomicalRegionComponent>(region, out var anatomical) ||
            !_prototypes.TryIndex(anatomical.TissueProfile, out NCTissueProfilePrototype? tissue) ||
            tissue.Layers.Count == 0)
        {
            return NCTissueDepth.Surface;
        }

        var index = Math.Clamp((int) MathF.Floor(Math.Clamp(depth, 0f, 0.9999f) * tissue.Layers.Count), 0, tissue.Layers.Count - 1);
        var layer = tissue.Layers[index];
        access = layer.AccessDepth;
        return layer.Kind switch
        {
            NCTissueLayerKind.SoftTissue => NCTissueDepth.SoftTissue,
            NCTissueLayerKind.Vascular => NCTissueDepth.Vascular,
            NCTissueLayerKind.Structural => NCTissueDepth.Structural,
            NCTissueLayerKind.Cavity => NCTissueDepth.Cavity,
            NCTissueLayerKind.InternalOrgan or NCTissueLayerKind.Mechanical => NCTissueDepth.InternalOrgan,
            _ => NCTissueDepth.Surface,
        };
    }

    private static Vector2 SurfaceDirection(NCWoundSurface surface, Vector2 stroke)
    {
        return surface switch
        {
            NCWoundSurface.Rear => -Vector2.UnitY,
            NCWoundSurface.Left => -Vector2.UnitX,
            NCWoundSurface.Right => Vector2.UnitX,
            _ => stroke.Normalized(),
        };
    }

    private static NCSurgicalIncisionRecord? FindCanonicalIncision(
        NCSurgicalSiteComponent site,
        ProtoId<NCTargetZonePrototype> zone,
        NCWoundSurface surface)
    {
        return site.Incisions
            .Where(incision => incision.TargetZone == zone && incision.Surface == surface)
            .OrderBy(incision => incision.CreatedAt)
            .ThenBy(incision => incision.IncisionId)
            .FirstOrDefault();
    }

    private static NCSurgicalIncisionRecord? FindOperativeSite(
        NCSurgicalSiteComponent site,
        ProtoId<NCTargetZonePrototype> zone,
        NCWoundSurface surface)
    {
        return site.Incisions
            .Where(incision => incision.TargetZone == zone && incision.Surface == surface && !incision.Sutured)
            .OrderBy(incision => incision.CreatedAt)
            .ThenBy(incision => incision.IncisionId)
            .FirstOrDefault();
    }

    private static void LinkWound(NCSurgicalIncisionRecord incision, Guid? woundId)
    {
        if (woundId is not { } linked || incision.WoundId == linked || incision.LinkedWoundIds.Contains(linked))
            return;
        if (incision.WoundId == null)
            incision.WoundId = linked;
        else
            incision.LinkedWoundIds.Add(linked);
    }

    private static IEnumerable<Guid> EnumerateWoundIds(NCSurgicalIncisionRecord incision)
    {
        if (incision.WoundId is { } primary)
            yield return primary;
        foreach (var linked in incision.LinkedWoundIds)
            yield return linked;
    }

    private static float PathLength(IReadOnlyList<Vector2> points)
    {
        var result = 0f;
        for (var i = 1; i < points.Count; i++)
            result += Vector2.Distance(points[i - 1], points[i]);
        return result;
    }

    private static Vector2 StrokeCenter(IReadOnlyList<Vector2> points)
    {
        var sum = Vector2.Zero;
        foreach (var point in points)
            sum += point;
        return sum / Math.Max(1, points.Count);
    }

    private static List<Vector2> Simplify(IReadOnlyList<Vector2> points, int maximum)
    {
        if (points.Count <= maximum)
            return [.. points];
        var result = new List<Vector2>(maximum);
        for (var i = 0; i < maximum; i++)
        {
            var index = (int) MathF.Round(i / (float) (maximum - 1) * (points.Count - 1));
            result.Add(points[index]);
        }
        return result;
    }

    private static void RecalculateAccess(NCSurgicalSiteComponent site, float minimumOpenRetraction)
    {
        var open = site.Incisions.Where(incision => !incision.Sutured).ToList();
        if (open.Count == 0)
        {
            site.AccessDepth = NCSurgicalAccessDepth.ClosedPostOp;
            site.TissuesRetracted = false;
            site.BoneCasingOpen = false;
            return;
        }

        site.TissuesRetracted = open.Any(incision => incision.EdgeSeparation >= minimumOpenRetraction);
        site.BoneCasingOpen = open.Any(incision => incision.BoneCasingOpen);
    }
}

public readonly record struct NCFreeformSurgeryEffectContext(
    EntityUid Surgeon,
    EntityUid Patient,
    EntityUid Region,
    EntityUid Tool,
    NCSurgeryToolComponent ToolComponent,
    ProtoId<NCTargetZonePrototype> Zone,
    NCWoundSurface Surface,
    float Depth,
    Vector2[] Points,
    NCFreeformSurgeryToolDefinition Definition,
    float ToolQuality,
    float ToolSterility,
    float UnpreparedContamination,
    float MinimumOpenRetraction,
    EntityUid? Structure,
    bool HasBoneTarget,
    bool IsCranialRegion,
    float IncidentalTendonRuptureThreshold,
    float IncidentalBoneCrackThreshold,
    float IncidentalBoneBreakThreshold,
    Vector2 ContactPosition = default,
    float? StructureMinimumDepth = null,
    bool StructureDetachableBySeparation = false,
    bool StructureBlocksDeeperStructures = false,
    Guid? WoundId = null);

public readonly record struct NCFreeformSurgeryEffectResult(bool Accepted, bool Applied, NCFreeformSurgeryFailure? Failure)
{
    public static readonly NCFreeformSurgeryEffectResult Rejected = new(false, false, NCFreeformSurgeryFailure.InvalidInput);
    public static readonly NCFreeformSurgeryEffectResult NoTarget = new(false, false, NCFreeformSurgeryFailure.NoApplicableTarget);
    public static readonly NCFreeformSurgeryEffectResult ResourceEmpty = new(false, false, NCFreeformSurgeryFailure.ResourceEmpty);
    public static readonly NCFreeformSurgeryEffectResult AccessClosed = new(false, false, NCFreeformSurgeryFailure.SurgicalAccessClosed);
}
