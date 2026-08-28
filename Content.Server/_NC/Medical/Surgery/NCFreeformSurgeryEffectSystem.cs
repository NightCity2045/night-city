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
            NCFreeformSurgeryAction.Retract => ApplyRetraction(context, site, center, power),
            NCFreeformSurgeryAction.Suture => ApplySuture(context, site, center, power),
            NCFreeformSurgeryAction.Irrigate => ApplyIrrigation(context, site, center, power),
            NCFreeformSurgeryAction.Debride => ApplyDebridement(context, site, center, power),
            NCFreeformSurgeryAction.RepairVessel => ApplyVascularRepair(context, center, power),
            NCFreeformSurgeryAction.RepairOrgan => ApplyOrganRepair(context, power),
            NCFreeformSurgeryAction.ReduceBone => ApplyBone(context, reduce: true),
            NCFreeformSurgeryAction.FixBone => ApplyBone(context, reduce: false),
            NCFreeformSurgeryAction.RepairTendon => ApplyTendon(context, power),
            NCFreeformSurgeryAction.RepairNerve => ApplyNerve(context, power),
            NCFreeformSurgeryAction.ExtractForeignBody => ApplyExtraction(context, center, resolution),
            NCFreeformSurgeryAction.OpenBone => ApplyBoneAccess(context, site, power),
            NCFreeformSurgeryAction.Separate => ApplySeparation(context),
            NCFreeformSurgeryAction.Amputate => ApplyAmputation(context),
            NCFreeformSurgeryAction.Transplant => ApplyTransplant(context),
            NCFreeformSurgeryAction.Cyberware => ApplyCyberware(context, resolution),
            NCFreeformSurgeryAction.DrainCavity => ApplyCavityDrain(context, power),
            _ => false,
        };

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

        var existing = FindIncision(site, context.Zone, context.Surface, center, context.Definition.Radius);
        if (existing != null && !existing.Sutured)
        {
            existing.Depth = MathF.Max(existing.Depth, context.Depth);
            existing.Width = MathF.Max(existing.Width, MathF.Max(0.01f, power * 0.004f));
            existing.Contamination = Math.Clamp(existing.Contamination + site.Contamination * 0.1f, 0f, 1f);
            existing.ModifiedAt = _timing.CurTime;
            existing.Polyline = Simplify(context.Points, 32);
            if (result.WoundId != null)
                existing.WoundId = result.WoundId;
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
            while (site.Incisions.Count > context.MaximumIncisions)
                site.Incisions.RemoveAt(0);
        }

        if (access > site.AccessDepth)
            site.AccessDepth = access;
        ApplyIncidentalStructureDamage(context, power);
        return true;
    }

    private void ApplyIncidentalStructureDamage(in NCFreeformSurgeryEffectContext context, float power)
    {
        if (context.Structure is not { } target || target == context.Region ||
            context.Definition.IncidentalStructureDamagePerPower <= 0f)
        {
            return;
        }

        var damage = power * context.Definition.IncidentalStructureDamagePerPower;
        var fraction = Math.Clamp(damage / 100f, 0f, 1f);
        if (TryComp<NCOrganIntegrityComponent>(target, out var integrity))
        {
            integrity.CurrentIntegrity = MathF.Max(0f, integrity.CurrentIntegrity - damage);
            integrity.FunctionalEfficiency = MathF.Min(
                integrity.FunctionalEfficiency,
                integrity.CurrentIntegrity / MathF.Max(1f, integrity.MaxIntegrity));
        }
        if (TryComp<NCOrganFunctionComponent>(target, out var function))
            function.CurrentCapacity = MathF.Max(0f, function.CurrentCapacity - fraction * function.BaseCapacity);
        if (TryComp<NCTendonComponent>(target, out var tendon))
        {
            tendon.Integrity = MathF.Max(0f, tendon.Integrity - fraction);
            tendon.Ruptured |= tendon.Integrity < context.IncidentalTendonRuptureThreshold;
        }
        if (TryComp<NCNerveComponent>(target, out var nerve))
        {
            nerve.NeuralIntegrity = MathF.Max(0f, nerve.NeuralIntegrity - fraction);
            nerve.MotorFunction = MathF.Min(nerve.MotorFunction, nerve.NeuralIntegrity);
        }
        if (TryComp<NCBoneComponent>(target, out var bone))
        {
            bone.Stability = MathF.Max(0f, bone.Stability - fraction);
            if (bone.Stability < context.IncidentalBoneBreakThreshold)
                bone.State = NCBoneState.BrokenStable;
            else if (bone.Stability < context.IncidentalBoneCrackThreshold && bone.State < NCBoneState.Cracked)
                bone.State = NCBoneState.Cracked;
        }
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
        Vector2 point,
        float power)
    {
        var incision = FindIncision(site, context.Zone, context.Surface, point, context.Definition.Radius);
        if (incision == null || incision.Sutured)
            return false;

        incision.EdgeSeparation = Math.Clamp(MathF.Max(incision.EdgeSeparation, power), 0f, 1f);
        incision.ModifiedAt = _timing.CurTime;
        site.TissuesRetracted = incision.EdgeSeparation >= 0.2f;
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
        var incision = FindIncision(site, context.Zone, context.Surface, point, context.Definition.Radius);
        if (incision == null)
            return false;

        incision.Width = MathF.Max(0f, incision.Width - power * 0.08f);
        incision.EdgeSeparation = MathF.Max(0f, incision.EdgeSeparation - power);
        incision.Sutured = incision.Width <= 0.012f || power >= 0.45f;
        incision.ModifiedAt = _timing.CurTime;
        if (incision.WoundId is { } woundId &&
            TryComp<NCWoundContainerComponent>(context.Patient, out var wounds) &&
            wounds.Wounds.TryGetValue(woundId, out var wound))
        {
            wound.ExternalBleedRate = 0f;
            wound.ClosureState = NCWoundClosureState.Sutured;
            wound.Stage = NCWoundStage.Treated;
            wound.ModifiedAt = _timing.CurTime;
            _bleeding.SynchronizeWoundSources(context.Patient, wound);
        }

        RecalculateAccess(site);
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

    private bool ApplyBone(in NCFreeformSurgeryEffectContext context, bool reduce)
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
        _hands.TryPickupAnyHand(context.Surgeon, extracted);
        return true;
    }

    private static bool ApplyBoneAccess(
        in NCFreeformSurgeryEffectContext context,
        NCSurgicalSiteComponent site,
        float power)
    {
        var target = context.Structure ?? context.Region;
        if (!context.HasBoneTarget && !context.IsCranialRegion)
            return false;
        site.BoneCasingOpen = power >= 0.2f;
        if (site.BoneCasingOpen && site.AccessDepth < NCSurgicalAccessDepth.EncasedStructureOpened)
            site.AccessDepth = NCSurgicalAccessDepth.EncasedStructureOpened;
        return true;
    }

    private bool ApplySeparation(in NCFreeformSurgeryEffectContext context)
    {
        // Organ separation is cursor-specific. Falling back to the selected external region would turn a
        // missed organ click into an accidental limb amputation.
        if (context.Structure is not { } target)
            return false;
        if (!TryComp<OrganComponent>(target, out var organ) || organ.Category is not { } category ||
            !_detachment.TryDetachOrgan(context.Patient, category, out var detached))
        {
            return false;
        }

        _hands.TryPickupAnyHand(context.Surgeon, detached);
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

        var incision = FindIncision(site, context.Zone, context.Surface, point, context.Definition.Radius);
        if (incision != null && !incision.Sutured && incision.Depth + 0.001f >= context.Depth)
            return true;

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

    private static NCSurgicalIncisionRecord? FindIncision(
        NCSurgicalSiteComponent site,
        ProtoId<NCTargetZonePrototype> zone,
        NCWoundSurface surface,
        Vector2 point,
        float radius)
    {
        NCSurgicalIncisionRecord? best = null;
        var bestDistance = radius * radius;
        foreach (var incision in site.Incisions)
        {
            if (incision.TargetZone != zone || incision.Surface != surface || incision.Polyline.Count == 0)
                continue;
            var distance = DistanceSquaredToPolyline(point, incision.Polyline);
            if (distance > bestDistance)
                continue;
            bestDistance = distance;
            best = incision;
        }
        return best;
    }

    private static float DistanceSquaredToPolyline(Vector2 point, IReadOnlyList<Vector2> polyline)
    {
        if (polyline.Count == 0)
            return float.MaxValue;
        if (polyline.Count == 1)
            return Vector2.DistanceSquared(point, polyline[0]);

        var best = float.MaxValue;
        for (var index = 1; index < polyline.Count; index++)
        {
            var start = polyline[index - 1];
            var segment = polyline[index] - start;
            var lengthSquared = segment.LengthSquared();
            var fraction = lengthSquared <= 0.000001f
                ? 0f
                : Math.Clamp(Vector2.Dot(point - start, segment) / lengthSquared, 0f, 1f);
            best = MathF.Min(best, Vector2.DistanceSquared(point, start + segment * fraction));
        }
        return best;
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

    private static void RecalculateAccess(NCSurgicalSiteComponent site)
    {
        var open = site.Incisions.Where(incision => !incision.Sutured).ToList();
        if (open.Count == 0)
        {
            site.AccessDepth = NCSurgicalAccessDepth.ClosedPostOp;
            site.TissuesRetracted = false;
            site.BoneCasingOpen = false;
            return;
        }

        site.TissuesRetracted = open.Any(incision => incision.EdgeSeparation >= 0.2f);
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
    int MaximumIncisions,
    EntityUid? Structure,
    bool HasBoneTarget,
    bool IsCranialRegion,
    float IncidentalTendonRuptureThreshold,
    float IncidentalBoneCrackThreshold,
    float IncidentalBoneBreakThreshold);

public readonly record struct NCFreeformSurgeryEffectResult(bool Accepted, bool Applied, NCFreeformSurgeryFailure? Failure)
{
    public static readonly NCFreeformSurgeryEffectResult Rejected = new(false, false, NCFreeformSurgeryFailure.InvalidInput);
    public static readonly NCFreeformSurgeryEffectResult NoTarget = new(false, false, NCFreeformSurgeryFailure.NoApplicableTarget);
    public static readonly NCFreeformSurgeryEffectResult ResourceEmpty = new(false, false, NCFreeformSurgeryFailure.ResourceEmpty);
    public static readonly NCFreeformSurgeryEffectResult AccessClosed = new(false, false, NCFreeformSurgeryFailure.SurgicalAccessClosed);
}
