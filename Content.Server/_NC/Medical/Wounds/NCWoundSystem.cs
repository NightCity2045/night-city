// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Numerics;
using Content.Server._NC.Medical.Bleeding;
using Content.Server._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Trauma;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Content.Shared.Damage.Prototypes;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Wounds;

/// <summary>
/// Accepts resolved physical impacts and creates authoritative local wounds. There is no accuracy roll,
/// RED check, global HP mutation or client-supplied entity involved at this boundary.
/// </summary>
public sealed partial class NCWoundSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCBleedingSystem _bleeding = default!;
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;
    [Dependency] private NCAnatomySystem _anatomy = default!;
    [Dependency] private NCCyberwareSystem _cyberware = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCWoundContainerComponent, ComponentInit>(OnContainerInit);
        SubscribeLocalEvent<NCMedicalBodyComponent, NCCombatTraumaRequestedEvent>(OnTraumaRequested);
    }

    private void OnContainerInit(Entity<NCWoundContainerComponent> entity, ref ComponentInit args)
    {
        // This component is attached lazily and owns server-runtime collections. Robust can recycle component
        // instances between deleted entities, so every new owner must start with isolated medical state.
        entity.Comp.Profile = default;
        entity.Comp.Wounds = new Dictionary<Guid, NCWoundRecord>();
        entity.Comp.Scars = [];
        entity.Comp.OverflowTraumaByZone = new();
        entity.Comp.OverflowPain = 0f;
        entity.Comp.ProcessedImpacts = new HashSet<NCProcessedImpactKey>();
        entity.Comp.ProcessedImpactOrder = new Queue<NCProcessedImpactKey>();
        entity.Comp.PendingMovementDistance = 0f;
        entity.Comp.LastProgressionPersistenceEventAt = TimeSpan.Zero;
    }

    private void OnTraumaRequested(
        Entity<NCMedicalBodyComponent> entity,
        ref NCCombatTraumaRequestedEvent args)
    {
        if (args.Cancelled)
            return;

        // Once the target is an authoritative medical body, vanilla HP damage must never be the fallback.
        args.Handled = true;
        if (args.Request.Target != entity.Owner)
        {
            args.Cancelled = true;
            return;
        }

        var result = ApplyTrauma(entity.Owner, args.Request);
        args.Applied = result.Created || result.Merged || result.Overflowed || result.MechanicalDamage;
        args.AppliedWoundType = result.WoundType;
        args.AppliedSeverity = result.Severity;
        args.AppliedExternalBleedRate = result.ExternalBleedRate;

        if (args.Applied &&
            result.WoundType is { } woundType &&
            result.Severity is { } severity)
        {
            // Secondary systems consume the accepted result instead of competing for the trauma-request subscription.
            var applied = new NCWoundImpactAppliedEvent(
                args.Request,
                woundType,
                severity,
                result.ExternalBleedRate);
            RaiseLocalEvent(entity.Owner, ref applied);
        }
    }

    /// <summary>
    /// Applies one server-resolved impact. Duplicate impact keys are accepted but produce no second wound.
    /// </summary>
    public NCWoundApplicationResult ApplyTrauma(EntityUid body, in NCCombatTraumaRequest request)
    {
        if (!request.ResolutionId.IsValid ||
            !TryComp<NCMedicalBodyComponent>(body, out var medical))
            return NCWoundApplicationResult.Rejected;

        var container = EnsureInitialized(body);
        if (!_prototypes.TryIndex(container.Profile, out var systemProfile) ||
            !_prototypes.TryIndex(request.TraumaProfile, out var traumaProfile))
        {
            return NCWoundApplicationResult.Rejected;
        }

        ProtoId<NCWoundPrototype> resolvedWoundPrototypeId;
        if (traumaProfile.WoundPrototype is { } overrideWound)
            resolvedWoundPrototypeId = overrideWound;
        else if (!systemProfile.Wounds.TryGetValue(traumaProfile.Mechanism, out resolvedWoundPrototypeId))
            return NCWoundApplicationResult.Rejected;

        if (!_prototypes.TryIndex(resolvedWoundPrototypeId, out var woundPrototype))
        {
            return NCWoundApplicationResult.Rejected;
        }

        if (!_anatomy.TryGetOrgan(body, request.MedicalRegion, out var region))
            return NCWoundApplicationResult.Rejected;

        // NC cyberware owns mechanical frame damage. It must never enter the organic wound, bleeding,
        // contamination or infection pipeline merely because it occupies a human anatomical category.
        if (TryComp<NCCyberwareComponent>(region, out _) &&
            TryComp<NCOrganIntegrityComponent>(region, out var mechanicalIntegrity) &&
            mechanicalIntegrity.Material == NCAnatomicalMaterialKind.Mechanical)
        {
            var mechanicalTrauma = request.PostArmorDamage > FixedPoint2.Zero
                ? request.PostArmorDamage
                : request.TransferredBluntDamage;
            if (mechanicalTrauma <= FixedPoint2.Zero)
                return NCWoundApplicationResult.AcceptedWithoutWound;

            var result = _cyberware.TryApplyMechanicalTrauma(
                body,
                region,
                request.ResolutionId,
                request.ProjectileSequence,
                request.ImpactSequence,
                mechanicalTrauma.Float());
            return result.Status switch
            {
                NCCyberwareMutationStatus.Applied =>
                    new NCWoundApplicationResult(true, false, false, false, null, true),
                NCCyberwareMutationStatus.Duplicate => NCWoundApplicationResult.Duplicate,
                _ => NCWoundApplicationResult.Rejected,
            };
        }

        if (IsDuplicate(container, request, systemProfile.ProcessedImpactCapacity))
            return NCWoundApplicationResult.Duplicate;

        // A stopped hit can still create a deterministic blunt-transfer wound. The impact adapter switches
        // that request to BluntImpact, so medicine consumes one authoritative scalar without a second check.
        var appliedTrauma = request.PostArmorDamage > FixedPoint2.Zero
            ? request.PostArmorDamage
            : request.TransferredBluntDamage;
        var traumaAmount = MathF.Max(0f, appliedTrauma.Float());
        if (traumaAmount <= 0f)
            return NCWoundApplicationResult.AcceptedWithoutWound;

        var now = request.Timestamp > TimeSpan.Zero ? request.Timestamp : _timing.CurTime;
        var severity = ResolveSeverity(traumaAmount, systemProfile.SeverityThresholds);
        var vascularDensity = 1f;
        var hasMajorArtery = false;
        var hasBodyCavity = false;
        if (TryComp<NCVascularRegionComponent>(region, out var vascular))
        {
            vascularDensity = MathF.Max(0f, vascular.VascularDensity);
            hasMajorArtery = vascular.HasMajorArtery;
            hasBodyCavity = vascular.Cavity != NCBodyCavity.None &&
                            HasComp<NCCavityFluidComponent>(region);
        }

        var wound = CreateWound(
            request,
            resolvedWoundPrototypeId,
            woundPrototype,
            traumaAmount,
            severity,
            now,
            vascularDensity,
            hasMajorArtery,
            hasBodyCavity);

        NCWoundRecord? exitWound = null;
        if (request.ProjectileDisposition == NCTraumaProjectileDisposition.Embedded)
        {
            // This GUID identifies the retained foreign body for surgery/forensics without persisting an entity.
            wound.EmbeddedObjectId = request.ResolutionId.DeriveGuid(
                request.ProjectileSequence ^ 0x40000000u,
                request.ImpactSequence);
        }
        else if (request.ProjectileDisposition == NCTraumaProjectileDisposition.Exit &&
                 float.IsFinite(request.ExitTraumaMultiplier) &&
                 request.ExitTraumaMultiplier > 0f)
        {
            var exitTrauma = traumaAmount * Math.Clamp(request.ExitTraumaMultiplier, 0f, 1f);
            if (exitTrauma > 0f)
            {
                exitWound = CreateWound(
                    request,
                    resolvedWoundPrototypeId,
                    woundPrototype,
                    exitTrauma,
                    ResolveSeverity(exitTrauma, systemProfile.SeverityThresholds),
                    now,
                    vascularDensity,
                    hasMajorArtery,
                    hasBodyCavity,
                    0x80000000u);
                exitWound.VisualX = (byte) (byte.MaxValue - wound.VisualX);
                exitWound.VisualDirection = QuantizeDirection(-request.ImpactDirection);
                wound.ExitWoundId = exitWound.WoundId;
                exitWound.ExitWoundId = wound.WoundId;
            }
        }

        ApplyLocalConsequences(
            body,
            region,
            request,
            systemProfile,
            woundPrototype,
            severity,
            traumaAmount);

        if (exitWound == null &&
            TryMerge(body, container, systemProfile, woundPrototype, wound, out var merged))
        {
            _bleeding.SynchronizeWoundSources(body, merged);
            MarkChanged(body, medical);
            return new NCWoundApplicationResult(
                true,
                false,
                true,
                false,
                merged.WoundId,
                WoundType: wound.WoundType,
                Severity: severity,
                ExternalBleedRate: wound.ExternalBleedRate);
        }

        var capacity = Math.Max(1, systemProfile.MaxActiveWounds);
        var requiredSlots = exitWound == null ? 1 : 2;
        if (container.Wounds.Count + requiredSlots > capacity)
        {
            container.OverflowTraumaByZone[wound.TargetZone] =
                container.OverflowTraumaByZone.GetValueOrDefault(wound.TargetZone) +
                (traumaAmount + (exitWound?.IntegrityDamage.Float() ?? 0f)) *
                MathF.Max(0f, woundPrototype.StatusDamageMultiplier);
            container.OverflowPain += wound.Pain + (exitWound?.Pain ?? 0f);
            _bleeding.AddOverflow(
                body,
                wound.ExternalBleedRate + (exitWound?.ExternalBleedRate ?? 0f),
                wound.InternalBleedRate + (exitWound?.InternalBleedRate ?? 0f),
                wound.RegionCategory);
            MarkChanged(body, medical);
            return new NCWoundApplicationResult(
                true,
                false,
                false,
                true,
                null,
                WoundType: wound.WoundType,
                Severity: severity,
                ExternalBleedRate: wound.ExternalBleedRate + (exitWound?.ExternalBleedRate ?? 0f));
        }

        container.Wounds.Add(wound.WoundId, wound);
        _bleeding.SynchronizeWoundSources(body, wound);
        if (exitWound != null)
        {
            container.Wounds.Add(exitWound.WoundId, exitWound);
            _bleeding.SynchronizeWoundSources(body, exitWound);
        }
        MarkChanged(body, medical);

        var created = new NCWoundCreatedEvent(
            wound.WoundId,
            wound.TargetZone,
            wound.Severity,
            wound.WoundType);
        RaiseLocalEvent(body, ref created);
        if (exitWound != null)
        {
            var exitCreated = new NCWoundCreatedEvent(
                exitWound.WoundId,
                exitWound.TargetZone,
                exitWound.Severity,
                exitWound.WoundType);
            RaiseLocalEvent(body, ref exitCreated);
        }
        return new NCWoundApplicationResult(
            true,
            true,
            false,
            false,
            wound.WoundId,
            WoundType: wound.WoundType,
            Severity: severity,
            ExternalBleedRate: wound.ExternalBleedRate + (exitWound?.ExternalBleedRate ?? 0f));
    }

    public NCWoundContainerComponent EnsureInitialized(EntityUid body)
    {
        var container = EnsureComp<NCWoundContainerComponent>(body);
        EnsureComp<NCBleedingComponent>(body);
        if (!string.IsNullOrWhiteSpace(container.Profile.Id))
            return container;

        var profiles = _prototypes.EnumeratePrototypes<NCWoundSystemProfilePrototype>()
            .Where(candidate => candidate.DefaultForHumans)
            .OrderBy(candidate => candidate.ID, StringComparer.Ordinal)
            .ToList();
        if (profiles.Count != 1)
            throw new InvalidOperationException("Exactly one default human ncWoundSystemProfile must be defined.");

        container.Profile = profiles[0].ID;
        return container;
    }

    public bool ResetBody(EntityUid body)
    {
        if (!HasComp<NCMedicalBodyComponent>(body))
            return false;

        var container = EnsureInitialized(body);
        container.Wounds.Clear();
        container.Scars.Clear();
        container.OverflowTraumaByZone.Clear();
        container.OverflowPain = 0f;
        container.PendingMovementDistance = 0f;
        container.LastProgressionPersistenceEventAt = TimeSpan.Zero;
        container.ProcessedImpacts.Clear();
        container.ProcessedImpactOrder.Clear();
        return true;
    }

    private bool TryMerge(
        EntityUid body,
        NCWoundContainerComponent container,
        NCWoundSystemProfilePrototype systemProfile,
        NCWoundPrototype prototype,
        NCWoundRecord incoming,
        out NCWoundRecord merged)
    {
        merged = default!;
        if (!prototype.CanMergeMinor ||
            incoming.Severity != NCWoundSeverity.Minor ||
            incoming.PrimaryBleedKind == NCBleedKind.Arterial ||
            incoming.EmbeddedObjectId != null ||
            incoming.ExitWoundId != null)
        {
            return false;
        }

        foreach (var existing in container.Wounds.Values)
        {
            if (existing.Prototype != incoming.Prototype ||
                existing.TargetZone != incoming.TargetZone ||
                existing.RegionCategory != incoming.RegionCategory ||
                existing.WoundType != incoming.WoundType ||
                existing.TissueDepth != incoming.TissueDepth ||
                existing.Severity != NCWoundSeverity.Minor ||
                existing.Stage != incoming.Stage ||
                existing.ClosureState != incoming.ClosureState ||
                existing.TreatmentFlags != incoming.TreatmentFlags ||
                existing.PrimaryBleedKind == NCBleedKind.Arterial ||
                existing.EmbeddedObjectId != null ||
                existing.ExitWoundId != null ||
                MathF.Abs(existing.Contamination - incoming.Contamination) >
                systemProfile.MergeContaminationTolerance)
            {
                continue;
            }

            var previousSeverity = existing.Severity;
            existing.IntegrityDamage += incoming.IntegrityDamage;
            existing.Area += incoming.Area;
            existing.ExternalBleedRate += incoming.ExternalBleedRate;
            existing.InternalBleedRate += incoming.InternalBleedRate;
            existing.InitialExternalBleedRate += incoming.InitialExternalBleedRate;
            existing.InitialInternalBleedRate += incoming.InitialInternalBleedRate;
            existing.Pain += incoming.Pain;
            existing.InitialPain += incoming.InitialPain;
            existing.Contamination = MathF.Max(existing.Contamination, incoming.Contamination);
            existing.ScarRisk = Math.Clamp(existing.ScarRisk + incoming.ScarRisk, 0f, 1f);
            existing.ModifiedAt = incoming.ModifiedAt;
            existing.Severity = ResolveSeverity(existing.IntegrityDamage.Float(), systemProfile.SeverityThresholds);
            existing.PrimaryBleedKind = ResolvePrimaryBleed(existing);
            merged = existing;

            var changed = new NCWoundChangedEvent(existing.WoundId, previousSeverity, existing.Severity);
            RaiseLocalEvent(body, ref changed);
            return true;
        }

        return false;
    }

    private static NCWoundRecord CreateWound(
        in NCCombatTraumaRequest request,
        ProtoId<NCWoundPrototype> prototypeId,
        NCWoundPrototype prototype,
        float traumaAmount,
        NCWoundSeverity severity,
        TimeSpan now,
        float vascularDensity,
        bool hasMajorArtery,
        bool hasBodyCavity,
        uint identitySalt = 0)
    {
        var externalRate = traumaAmount * MathF.Max(0f, prototype.ExternalBleedPerDamage) * vascularDensity;
        var arterial = hasMajorArtery &&
                       prototype.ArterialFromSeverity is { } arterialSeverity &&
                       severity >= arterialSeverity;
        if (arterial)
            externalRate *= MathF.Max(0f, prototype.ArterialRateMultiplier);

        // Internal circulation loss is cavity-local. Closed limb bruising remains a wound/hematoma but
        // cannot silently drain blood into a non-existent global internal pool.
        var internalRate = hasBodyCavity
            ? traumaAmount * MathF.Max(0f, prototype.InternalBleedPerDamage) * vascularDensity
            : 0f;
        var primaryBleedKind = externalRate > 0f
            ? arterial
                ? NCBleedKind.Arterial
                : severity == NCWoundSeverity.Minor
                    ? NCBleedKind.Capillary
                    : NCBleedKind.Venous
            : internalRate > 0f
                ? NCBleedKind.Internal
                : NCBleedKind.None;

        return new NCWoundRecord
        {
            // Stable action-derived IDs make persistence/replay deterministic without introducing a gameplay roll.
            WoundId = request.ResolutionId.DeriveGuid(
                request.ProjectileSequence ^ identitySalt,
                request.ImpactSequence),
            Prototype = prototypeId,
            TargetZone = request.ActualZone,
            RegionCategory = request.MedicalRegion,
            PrimaryDamageType = FindPrimaryDamageType(request),
            WoundType = prototype.WoundType,
            TissueDepth = prototype.TissueDepth,
            Severity = severity,
            IntegrityDamage = FixedPoint2.New(traumaAmount),
            Area = traumaAmount * MathF.Max(0f, prototype.AreaPerDamage),
            ExternalBleedRate = externalRate,
            InternalBleedRate = internalRate,
            InitialExternalBleedRate = externalRate,
            InitialInternalBleedRate = internalRate,
            PrimaryBleedKind = primaryBleedKind,
            Pain = traumaAmount * MathF.Max(0f, prototype.PainPerDamage),
            InitialPain = traumaAmount * MathF.Max(0f, prototype.PainPerDamage),
            Contamination = Math.Clamp(request.Contamination * prototype.ContaminationMultiplier, 0f, 1f),
            ScarRisk = Math.Clamp(traumaAmount * prototype.ScarRiskPerDamage, 0f, 1f),
            ForensicResolutionId = request.ResolutionId.Value,
            VisualX = QuantizeSurfaceCoordinate(request.TargetLocalHitPosition.X + 0.5f),
            VisualY = QuantizeSurfaceCoordinate(0.5f - request.TargetLocalHitPosition.Y),
            VisualDirection = QuantizeDirection(request.ImpactDirection),
            CreatedAt = now,
            ModifiedAt = now,
        };
    }

    private static byte QuantizeSurfaceCoordinate(float value)
    {
        value = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0.5f;
        return (byte) Math.Clamp((int) MathF.Round(value * byte.MaxValue), 0, byte.MaxValue);
    }

    private static byte QuantizeDirection(Vector2 direction)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || direction.IsLengthZero())
            return 0;

        var angle = MathF.Atan2(direction.Y, direction.X);
        var normalized = (angle + MathF.PI) / (MathF.PI * 2f);
        return QuantizeSurfaceCoordinate(normalized);
    }

    private void ApplyLocalConsequences(
        EntityUid body,
        EntityUid region,
        in NCCombatTraumaRequest request,
        NCWoundSystemProfilePrototype systemProfile,
        NCWoundPrototype prototype,
        NCWoundSeverity severity,
        float traumaAmount)
    {
        ApplyIntegrity(region, traumaAmount * MathF.Max(0f, prototype.RegionIntegrityDamageMultiplier));
        ApplyFunction(region, traumaAmount, prototype.RegionIntegrityDamageMultiplier, prototype);
        ApplyNerve(region, severity, traumaAmount, prototype);
        ApplyJoint(region, severity, traumaAmount, prototype);

        if (!TryComp<InitialBodyComponent>(body, out var initial))
            return;

        // Copy the read-only component field before querying the collection; the anatomy system remains the
        // sole owner of relationship mutation.
        var relationships = initial.Relationships;
        if (relationships == null ||
            !relationships.TryGetValue(request.MedicalRegion, out var childCategories))
        {
            return;
        }

        var structures = new List<EntityUid>();
        var functionalOrgans = new List<EntityUid>();
        var tendons = new List<EntityUid>();
        foreach (var category in childCategories.OrderBy(category => category.Id, StringComparer.Ordinal))
        {
            if (!_anatomy.TryGetOrgan(body, category, out var child))
                continue;

            if (HasComp<NCBoneComponent>(child))
                structures.Add(child);
            if (HasComp<NCOrganFunctionComponent>(child))
                functionalOrgans.Add(child);
            if (HasComp<NCTendonComponent>(child))
                tendons.Add(child);
        }

        if (prototype.StructuralDamageFromSeverity is { } structureThreshold &&
            severity >= structureThreshold &&
            prototype.ContainedStructureDamageMultiplier > 0f)
        {
            foreach (var child in SelectDeterministic(
                         structures,
                         prototype.ContainedTargetCount,
                         request,
                         0xA24BAED4963EE407UL))
            {
                ApplyIntegrity(child, traumaAmount * prototype.ContainedStructureDamageMultiplier);
                ApplyBoneState(
                    child,
                    severity,
                    traumaAmount,
                    prototype.ContainedStructureDamageMultiplier,
                    systemProfile);
            }
        }

        if (prototype.ContainedOrganDamageFromSeverity is { } organThreshold &&
            severity >= organThreshold &&
            prototype.ContainedOrganDamageMultiplier > 0f)
        {
            foreach (var child in SelectDeterministic(
                         functionalOrgans,
                         prototype.ContainedTargetCount,
                         request,
                         0x9FB21C651E98DF25UL))
            {
                ApplyIntegrity(child, traumaAmount * prototype.ContainedOrganDamageMultiplier);
                ApplyFunction(child, traumaAmount, prototype.ContainedOrganDamageMultiplier, prototype);
            }
        }

        if (prototype.TendonDamageFromSeverity is { } tendonThreshold &&
            severity >= tendonThreshold &&
            prototype.TendonIntegrityLossPerDamage > 0f)
        {
            foreach (var child in SelectDeterministic(
                         tendons,
                         prototype.ContainedTargetCount,
                         request,
                         0xD6E8FEB86659FD93UL))
            {
                ApplyTendon(child, traumaAmount, prototype);
            }
        }
    }

    private void ApplyIntegrity(EntityUid organ, float damage)
    {
        if (damage <= 0f || !TryComp<NCOrganIntegrityComponent>(organ, out var integrity))
            return;

        integrity.CurrentIntegrity = Math.Clamp(
            integrity.CurrentIntegrity - damage,
            0f,
            MathF.Max(0f, integrity.MaxIntegrity));
        integrity.FunctionalEfficiency = integrity.MaxIntegrity > 0f
            ? Math.Clamp(integrity.CurrentIntegrity / integrity.MaxIntegrity, 0f, 1f)
            : 0f;
    }

    private void ApplyFunction(
        EntityUid organ,
        float traumaAmount,
        float damageMultiplier,
        NCWoundPrototype prototype)
    {
        if (!TryComp<NCOrganFunctionComponent>(organ, out var function))
            return;

        var loss = traumaAmount * MathF.Max(0f, damageMultiplier) *
                   MathF.Max(0f, prototype.FunctionLossPerDamage);
        function.CurrentCapacity = Math.Clamp(
            function.CurrentCapacity - loss,
            0f,
            MathF.Max(0f, function.BaseCapacity));
    }

    private void ApplyBoneState(
        EntityUid organ,
        NCWoundSeverity severity,
        float traumaAmount,
        float multiplier,
        NCWoundSystemProfilePrototype systemProfile)
    {
        if (!TryComp<NCBoneComponent>(organ, out var bone))
            return;

        if (!systemProfile.BoneStateBySeverity.TryGetValue(severity, out var state))
            return;
        if (state > bone.State)
            bone.State = state;

        bone.Stability = Math.Clamp(
            bone.Stability - traumaAmount * multiplier * systemProfile.BoneStabilityLossPerDamage,
            0f,
            1f);
        bone.Displacement = state >= systemProfile.DisplacementFromState
            ? MathF.Max(bone.Displacement, 1f - bone.Stability)
            : bone.Displacement;
        bone.RequiresFixation |= state >= systemProfile.FixationFromState;
    }

    private void ApplyTendon(EntityUid organ, float traumaAmount, NCWoundPrototype prototype)
    {
        if (!TryComp<NCTendonComponent>(organ, out var tendon))
            return;

        tendon.Integrity = Math.Clamp(
            tendon.Integrity - traumaAmount * prototype.TendonIntegrityLossPerDamage,
            0f,
            1f);
        tendon.Ruptured |= tendon.Integrity <= prototype.TendonRuptureIntegrityThreshold;
    }

    private void ApplyNerve(
        EntityUid region,
        NCWoundSeverity severity,
        float traumaAmount,
        NCWoundPrototype prototype)
    {
        if (prototype.NerveDamageFromSeverity is not { } threshold ||
            severity < threshold ||
            !TryComp<NCNerveComponent>(region, out var nerve))
        {
            return;
        }

        nerve.NeuralIntegrity = Math.Clamp(
            nerve.NeuralIntegrity - traumaAmount * prototype.NerveIntegrityLossPerDamage,
            0f,
            1f);
        nerve.MotorFunction = Math.Clamp(
            nerve.MotorFunction - traumaAmount * prototype.NerveMotorLossPerDamage,
            0f,
            1f);
    }

    private void ApplyJoint(
        EntityUid region,
        NCWoundSeverity severity,
        float traumaAmount,
        NCWoundPrototype prototype)
    {
        if (prototype.JointDamageFromSeverity is not { } threshold ||
            severity < threshold ||
            !TryComp<NCJointComponent>(region, out var joint))
        {
            return;
        }

        joint.Stability = Math.Clamp(
            joint.Stability - traumaAmount * prototype.JointStabilityLossPerDamage,
            0f,
            1f);
        joint.MovementRestriction = MathF.Max(joint.MovementRestriction, 1f - joint.Stability);
        joint.Dislocated |= joint.Stability <= prototype.JointDislocationStabilityThreshold;
    }

    private static IEnumerable<EntityUid> SelectDeterministic(
        IReadOnlyList<EntityUid> candidates,
        int configuredCount,
        NCCombatTraumaRequest request,
        ulong salt)
    {
        if (candidates.Count == 0 || configuredCount <= 0)
            yield break;

        var bytes = request.ResolutionId.Value.ToByteArray();
        var seed = BitConverter.ToUInt64(bytes, 0) ^
                   ((ulong) request.ProjectileSequence << 32) ^
                   request.ImpactSequence ^ salt;
        var start = (int) (SplitMix64(seed) % (ulong) candidates.Count);
        var count = Math.Min(configuredCount, candidates.Count);
        for (var i = 0; i < count; i++)
            yield return candidates[(start + i) % candidates.Count];
    }

    private static ulong SplitMix64(ulong value)
    {
        value += 0x9E3779B97F4A7C15UL;
        value = (value ^ value >> 30) * 0xBF58476D1CE4E5B9UL;
        value = (value ^ value >> 27) * 0x94D049BB133111EBUL;
        return value ^ value >> 31;
    }

    private static ProtoId<DamageTypePrototype>? FindPrimaryDamageType(in NCCombatTraumaRequest request)
    {
        ProtoId<DamageTypePrototype>? result = null;
        var greatest = FixedPoint2.Zero;
        foreach (var (damageType, amount) in request.Damage.DamageDict)
        {
            if (amount < greatest ||
                amount == greatest && result is { } current &&
                string.CompareOrdinal(damageType.Id, current.Id) >= 0)
            {
                continue;
            }

            greatest = amount;
            result = damageType;
        }

        return result;
    }

    private static NCWoundSeverity ResolveSeverity(float traumaAmount, IReadOnlyList<float> thresholds)
    {
        if (thresholds.Count != 4 || !IsStrictlyAscending(thresholds))
            throw new InvalidOperationException("NC wound severity thresholds must contain exactly four values.");

        if (traumaAmount <= thresholds[0])
            return NCWoundSeverity.Minor;
        if (traumaAmount <= thresholds[1])
            return NCWoundSeverity.Moderate;
        if (traumaAmount <= thresholds[2])
            return NCWoundSeverity.Severe;
        if (traumaAmount <= thresholds[3])
            return NCWoundSeverity.Critical;
        return NCWoundSeverity.Catastrophic;
    }

    private static bool IsStrictlyAscending(IReadOnlyList<float> values)
    {
        for (var i = 1; i < values.Count; i++)
        {
            if (!float.IsFinite(values[i - 1]) || values[i] <= values[i - 1])
                return false;
        }
        return values.Count == 0 || float.IsFinite(values[^1]);
    }

    private static NCBleedKind ResolvePrimaryBleed(NCWoundRecord wound)
    {
        if (wound.PrimaryBleedKind == NCBleedKind.Arterial)
            return NCBleedKind.Arterial;
        if (wound.ExternalBleedRate > 0f)
            return wound.Severity == NCWoundSeverity.Minor ? NCBleedKind.Capillary : NCBleedKind.Venous;
        return wound.InternalBleedRate > 0f ? NCBleedKind.Internal : NCBleedKind.None;
    }

    private static bool IsDuplicate(
        NCWoundContainerComponent container,
        in NCCombatTraumaRequest request,
        int configuredCapacity)
    {
        if (!request.ResolutionId.IsValid)
            return false;

        var key = new NCProcessedImpactKey(
            request.ResolutionId.Value,
            request.ProjectileSequence,
            request.ImpactSequence);
        if (!container.ProcessedImpacts.Add(key))
            return true;

        var capacity = Math.Max(1, configuredCapacity);
        while (container.ProcessedImpactOrder.Count >= capacity)
            container.ProcessedImpacts.Remove(container.ProcessedImpactOrder.Dequeue());
        container.ProcessedImpactOrder.Enqueue(key);
        return false;
    }

    private void MarkChanged(EntityUid body, NCMedicalBodyComponent medical)
    {
        var domains = NCMedicalDirtyDomains.Wounds |
                      NCMedicalDirtyDomains.Vascular |
                      NCMedicalDirtyDomains.Pain |
                      NCMedicalDirtyDomains.OrganFunction |
                      NCMedicalDirtyDomains.Consciousness |
                      NCMedicalDirtyDomains.Diagnostics |
                      NCMedicalDirtyDomains.Persistence;
        _medicalBodies.TryMarkDirty((body, medical), domains);
    }
}

public readonly record struct NCWoundApplicationResult(
    bool Accepted,
    bool Created,
    bool Merged,
    bool Overflowed,
    Guid? WoundId,
    bool MechanicalDamage = false,
    NCWoundType? WoundType = null,
    NCWoundSeverity? Severity = null,
    float ExternalBleedRate = 0f)
{
    public static NCWoundApplicationResult Rejected => new(false, false, false, false, null);
    public static NCWoundApplicationResult Duplicate => new(true, false, false, false, null);
    public static NCWoundApplicationResult AcceptedWithoutWound => new(true, false, false, false, null);
}
