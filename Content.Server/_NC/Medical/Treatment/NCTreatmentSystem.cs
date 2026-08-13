// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared.Body;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Treatment;
using Content.Shared._NC.Medical.Wounds;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Treatment;

/// <summary>
/// Applies deterministic first aid to authoritative NC wounds. Every operation addresses one stable
/// wound Guid or a data-defined limb group, derives its effect from current server bleeding sources and
/// wakes the central physiology scheduler through the standard medical dirty event.
/// </summary>
public sealed partial class NCTreatmentSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;
    [Dependency] private NCAnatomySystem _anatomy = default!;
    [Dependency] private OrganRelationSystem _organRelations = default!;

    public override void Initialize()
    {
        base.Initialize();

        // Wound creation and surgery can replace a source's CurrentRate with its measured BaseRate.
        // Reapplying treatment from stable wound flags here keeps the two systems composable.
        SubscribeLocalEvent<NCTreatmentStateComponent, NCMedicalBodyChangedEvent>(OnMedicalBodyChanged);
        SubscribeLocalEvent<NCTreatmentStateComponent, NCBleedingChangedEvent>(OnBleedingChanged);
    }

    private void OnMedicalBodyChanged(
        Entity<NCTreatmentStateComponent> entity,
        ref NCMedicalBodyChangedEvent args)
    {
        if ((args.Domains & (NCMedicalDirtyDomains.Wounds | NCMedicalDirtyDomains.Vascular)) == 0 ||
            !TryGetProfile(entity.Comp, out var profile))
        {
            return;
        }

        RecalculateSources(entity.Owner, entity.Comp, profile, _timing.CurTime);
    }

    private void OnBleedingChanged(
        Entity<NCTreatmentStateComponent> entity,
        ref NCBleedingChangedEvent args)
    {
        if (!TryGetProfile(entity.Comp, out var profile) ||
            !RecalculateSources(entity.Owner, entity.Comp, profile, _timing.CurTime))
        {
            return;
        }

        // Time-based rebound expiry is discovered while the already-scheduled body is processed. Do not
        // raise a second immediate scheduling event from inside that scheduler pass.
        _medicalBodies.TryMarkDirty(
            entity.Owner,
            NCMedicalDirtyDomains.Vascular |
            NCMedicalDirtyDomains.Consciousness |
            NCMedicalDirtyDomains.Diagnostics |
            NCMedicalDirtyDomains.Persistence,
            false);
    }

    public NCTreatmentResult ApplyPressure(
        EntityUid patient,
        Guid woundId,
        TimeSpan? timestamp = null,
        ProtoId<NCTreatmentProfilePrototype>? profileOverride = null)
    {
        var context = CreateActionContext(timestamp);
        return ApplyPressure(patient, woundId, context, profileOverride);
    }

    public NCTreatmentResult ApplyPressure(
        EntityUid patient,
        Guid woundId,
        in NCTreatmentActionContext context,
        ProtoId<NCTreatmentProfilePrototype>? profileOverride = null)
    {
        return ApplyWoundTreatment(
            patient,
            woundId,
            NCTreatmentKind.Pressure,
            NCWoundTreatmentFlags.Pressure,
            context,
            profileOverride);
    }

    public NCTreatmentResult BandageWound(
        EntityUid patient,
        Guid woundId,
        TimeSpan? timestamp = null,
        ProtoId<NCTreatmentProfilePrototype>? profileOverride = null)
    {
        var context = CreateActionContext(timestamp);
        return BandageWound(patient, woundId, context, profileOverride);
    }

    public NCTreatmentResult BandageWound(
        EntityUid patient,
        Guid woundId,
        in NCTreatmentActionContext context,
        ProtoId<NCTreatmentProfilePrototype>? profileOverride = null)
    {
        return ApplyWoundTreatment(
            patient,
            woundId,
            NCTreatmentKind.Bandage,
            NCWoundTreatmentFlags.Bandaged,
            context,
            profileOverride);
    }

    public NCTreatmentResult ApplyHemostatic(
        EntityUid patient,
        Guid woundId,
        TimeSpan? timestamp = null,
        ProtoId<NCTreatmentProfilePrototype>? profileOverride = null)
    {
        var context = CreateActionContext(timestamp);
        return ApplyHemostatic(patient, woundId, context, profileOverride);
    }

    public NCTreatmentResult ApplyHemostatic(
        EntityUid patient,
        Guid woundId,
        in NCTreatmentActionContext context,
        ProtoId<NCTreatmentProfilePrototype>? profileOverride = null)
    {
        return ApplyWoundTreatment(
            patient,
            woundId,
            NCTreatmentKind.Hemostatic,
            NCWoundTreatmentFlags.Hemostatic,
            context,
            profileOverride);
    }

    /// <summary>
    /// Releases manually maintained pressure without disturbing a bandage or hemostatic packing.
    /// </summary>
    public NCTreatmentResult ReleasePressure(
        EntityUid patient,
        Guid woundId,
        TimeSpan? timestamp = null)
    {
        var context = CreateActionContext(timestamp);
        return ReleasePressure(patient, woundId, context);
    }

    public NCTreatmentResult ReleasePressure(
        EntityUid patient,
        Guid woundId,
        in NCTreatmentActionContext context)
    {
        if (!HasComp<NCMedicalBodyComponent>(patient))
            return new NCTreatmentResult(NCTreatmentOutcome.PatientNotAuthoritative, woundId, null, 0f, 0f);
        if (!TryEnsureStateAndProfile(patient, null, out var state, out var profile))
            return new NCTreatmentResult(NCTreatmentOutcome.InvalidProfile, woundId, null, 0f, 0f);

        if (!context.ResolutionId.IsValid)
            return new NCTreatmentResult(NCTreatmentOutcome.InvalidAction, woundId, null, 0f, 0f);
        if (TryGetDuplicate(state, context, out var duplicate))
            return duplicate;
        if (!TryGetWound(patient, woundId, out var wound, out _))
        {
            var missing = new NCTreatmentResult(NCTreatmentOutcome.WoundNotFound, woundId, null, 0f, 0f);
            return CompleteAction(patient, state, profile, context, NCTreatmentKind.PressureRelease, missing);
        }

        if ((wound.TreatmentFlags & NCWoundTreatmentFlags.Pressure) == 0)
        {
            var unchanged = new NCTreatmentResult(
                NCTreatmentOutcome.AlreadyApplied,
                woundId,
                wound.TargetZone,
                GetWoundRate(patient, woundId),
                GetWoundRate(patient, woundId));
            return CompleteAction(patient, state, profile, context, NCTreatmentKind.PressureRelease, unchanged);
        }

        var now = context.Timestamp;
        var previous = GetWoundRate(patient, woundId);
        wound.TreatmentFlags &= ~NCWoundTreatmentFlags.Pressure;
        wound.ModifiedAt = now;
        if (state.WoundTreatments.TryGetValue(woundId, out var treatment))
        {
            treatment.AppliedFlags &= ~NCWoundTreatmentFlags.Pressure;
            treatment.ModifiedAt = now;
        }

        RecalculateSources(patient, state, profile, now);
        var current = GetWoundRate(patient, woundId);
        var result = new NCTreatmentResult(NCTreatmentOutcome.Applied, woundId, wound.TargetZone, previous, current);
        CompleteAction(patient, state, profile, context, NCTreatmentKind.PressureRelease, result);
        MarkChanged(patient, context.ResolutionId, NCTreatmentKind.PressureRelease, woundId, wound.TargetZone, previous, current);
        return result;
    }

    public NCTreatmentResult SetTourniquet(
        EntityUid patient,
        ProtoId<NCTargetZonePrototype> zone,
        TimeSpan? timestamp = null,
        ProtoId<NCTreatmentProfilePrototype>? profileOverride = null)
    {
        var context = CreateActionContext(timestamp);
        return SetTourniquet(patient, zone, context, profileOverride);
    }

    public NCTreatmentResult SetTourniquet(
        EntityUid patient,
        ProtoId<NCTargetZonePrototype> zone,
        in NCTreatmentActionContext context,
        ProtoId<NCTreatmentProfilePrototype>? profileOverride = null)
    {
        if (!HasComp<NCMedicalBodyComponent>(patient))
            return new NCTreatmentResult(NCTreatmentOutcome.PatientNotAuthoritative, null, zone, 0f, 0f);

        if (!TryEnsureStateAndProfile(patient, profileOverride, out var state, out var profile))
            return new NCTreatmentResult(NCTreatmentOutcome.InvalidProfile, null, zone, 0f, 0f);

        if (!context.ResolutionId.IsValid)
            return new NCTreatmentResult(NCTreatmentOutcome.InvalidAction, null, zone, 0f, 0f);
        if (TryGetDuplicate(state, context, out var duplicate))
            return duplicate;

        if (!profile.TourniquetZoneRoots.TryGetValue(zone, out var root))
        {
            var invalid = new NCTreatmentResult(NCTreatmentOutcome.InvalidTourniquetZone, null, zone, 0f, 0f);
            return CompleteAction(patient, state, profile, context, NCTreatmentKind.Tourniquet, invalid);
        }

        if (state.Tourniquets.TryGetValue(root, out var existing) && existing.Active)
        {
            var existingRate = GetLimbRate(patient, profile, root);
            var alreadyApplied = new NCTreatmentResult(
                NCTreatmentOutcome.TourniquetAlreadyApplied,
                null,
                root,
                existingRate,
                existingRate);
            return CompleteAction(patient, state, profile, context, NCTreatmentKind.Tourniquet, alreadyApplied);
        }

        if (!TryComp<NCBleedingComponent>(patient, out var bleeding))
        {
            var noBleeding = new NCTreatmentResult(NCTreatmentOutcome.NoExternalBleeding, null, root, 0f, 0f);
            return CompleteAction(patient, state, profile, context, NCTreatmentKind.Tourniquet, noBleeding);
        }

        var coveredWounds = bleeding.Sources
            .Where(source => IsExternal(source.Channel) &&
                             source.BaseRate > 0f &&
                             IsInLimb(profile, source.TargetZone, root))
            .Select(source => source.WoundId)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        if (coveredWounds.Count == 0)
        {
            var noBleeding = new NCTreatmentResult(NCTreatmentOutcome.NoExternalBleeding, null, root, 0f, 0f);
            return CompleteAction(patient, state, profile, context, NCTreatmentKind.Tourniquet, noBleeding);
        }

        var now = context.Timestamp;
        var previous = GetLimbRate(patient, profile, root);
        var record = new NCTourniquetRecord
        {
            RootZone = root,
            CoveredWoundIds = coveredWounds,
            Active = true,
            AppliedAt = now,
            IschemiaRiskAt = now + profile.IschemiaRiskAfter,
            CriticalIschemiaAt = now + profile.CriticalIschemiaAfter,
        };
        state.Tourniquets[root] = record;

        if (TryComp<NCWoundContainerComponent>(patient, out var wounds))
        {
            foreach (var woundId in coveredWounds)
            {
                if (!wounds.Wounds.TryGetValue(woundId, out var wound))
                    continue;

                wound.TreatmentFlags |= NCWoundTreatmentFlags.Tourniqueted;
                wound.Stage = NCWoundStage.Treated;
                wound.ModifiedAt = now;
                EnsureWoundTreatment(state, wound, now).AppliedFlags = wound.TreatmentFlags;
            }
        }

        RecalculateSources(patient, state, profile, now);
        var current = GetLimbRate(patient, profile, root);
        var result = new NCTreatmentResult(NCTreatmentOutcome.Applied, null, root, previous, current);
        CompleteAction(patient, state, profile, context, NCTreatmentKind.Tourniquet, result);
        MarkChanged(patient, context.ResolutionId, NCTreatmentKind.Tourniquet, null, root, previous, current);
        return result;
    }

    /// <summary>
    /// Removes a tourniquet. Once the configured safety threshold is crossed, callers must explicitly
    /// request controlled release; an ordinary removal attempt is rejected without mutating the patient.
    /// </summary>
    public NCTreatmentResult RemoveTourniquet(
        EntityUid patient,
        ProtoId<NCTargetZonePrototype> zone,
        bool controlledRelease = false,
        TimeSpan? timestamp = null)
    {
        var context = CreateActionContext(timestamp);
        return RemoveTourniquet(patient, zone, context, controlledRelease);
    }

    public NCTreatmentResult RemoveTourniquet(
        EntityUid patient,
        ProtoId<NCTargetZonePrototype> zone,
        in NCTreatmentActionContext context,
        bool controlledRelease = false)
    {
        if (!HasComp<NCMedicalBodyComponent>(patient))
            return new NCTreatmentResult(NCTreatmentOutcome.PatientNotAuthoritative, null, zone, 0f, 0f);

        if (!TryEnsureStateAndProfile(patient, null, out var state, out var profile))
        {
            return new NCTreatmentResult(NCTreatmentOutcome.InvalidProfile, null, zone, 0f, 0f);
        }

        if (!context.ResolutionId.IsValid)
            return new NCTreatmentResult(NCTreatmentOutcome.InvalidAction, null, zone, 0f, 0f);
        if (TryGetDuplicate(state, context, out var duplicate))
            return duplicate;

        if (!profile.TourniquetZoneRoots.TryGetValue(zone, out var root))
        {
            var invalid = new NCTreatmentResult(NCTreatmentOutcome.InvalidTourniquetZone, null, zone, 0f, 0f);
            return CompleteAction(patient, state, profile, context, NCTreatmentKind.TourniquetRelease, invalid);
        }

        if (!state.Tourniquets.TryGetValue(root, out var record) || !record.Active)
        {
            var existingRate = GetLimbRate(patient, profile, root);
            var missing = new NCTreatmentResult(
                NCTreatmentOutcome.TourniquetNotApplied,
                null,
                root,
                existingRate,
                existingRate);
            return CompleteAction(patient, state, profile, context, NCTreatmentKind.TourniquetRelease, missing);
        }

        var now = context.Timestamp;
        var elapsed = now > record.AppliedAt ? now - record.AppliedAt : TimeSpan.Zero;
        if (elapsed >= profile.ControlledReleaseAfter && !controlledRelease)
        {
            var protectedRate = GetLimbRate(patient, profile, root);
            var unsafeRelease = new NCTreatmentResult(
                NCTreatmentOutcome.UnsafeReleaseRequiresControlled,
                null,
                root,
                protectedRate,
                protectedRate);
            return CompleteAction(
                patient,
                state,
                profile,
                context,
                NCTreatmentKind.TourniquetRelease,
                unsafeRelease);
        }

        var previous = GetLimbRate(patient, profile, root);
        record.Active = false;
        record.RemovedAt = now;
        record.ReboundEndsAt = now + profile.ReleaseReboundDuration;
        record.ControlledRelease = controlledRelease;

        if (TryComp<NCWoundContainerComponent>(patient, out var wounds))
        {
            foreach (var wound in wounds.Wounds.Values)
            {
                if (!IsInLimb(profile, wound.TargetZone, root))
                    continue;

                wound.TreatmentFlags &= ~NCWoundTreatmentFlags.Tourniqueted;
                wound.ModifiedAt = now;
                if (state.WoundTreatments.TryGetValue(wound.WoundId, out var treatment))
                {
                    treatment.AppliedFlags = wound.TreatmentFlags;
                    treatment.ModifiedAt = now;
                }
            }
        }

        RecalculateSources(patient, state, profile, now);
        var current = GetLimbRate(patient, profile, root);
        var result = new NCTreatmentResult(NCTreatmentOutcome.Applied, null, root, previous, current);
        CompleteAction(patient, state, profile, context, NCTreatmentKind.TourniquetRelease, result);
        MarkChanged(patient, context.ResolutionId, NCTreatmentKind.TourniquetRelease, null, root, previous, current);
        return result;
    }

    public bool TryGetTourniquetStatus(
        EntityUid patient,
        ProtoId<NCTargetZonePrototype> zone,
        out NCTourniquetStatus status,
        TimeSpan? timestamp = null)
    {
        status = default;
        if (!TryComp<NCTreatmentStateComponent>(patient, out var state) ||
            !TryGetProfile(state, out var profile) ||
            !profile.TourniquetZoneRoots.TryGetValue(zone, out var root) ||
            !state.Tourniquets.TryGetValue(root, out var record))
        {
            return false;
        }

        var now = timestamp ?? _timing.CurTime;
        var elapsed = now > record.AppliedAt ? now - record.AppliedAt : TimeSpan.Zero;
        var ischemia = !record.Active
            ? NCTourniquetIschemiaState.None
            : now >= record.CriticalIschemiaAt
                ? NCTourniquetIschemiaState.Critical
                : now >= record.IschemiaRiskAt
                    ? NCTourniquetIschemiaState.AtRisk
                    : NCTourniquetIschemiaState.None;
        status = new NCTourniquetStatus(
            record.Active,
            ischemia,
            record.AppliedAt,
            elapsed,
            record.Active && elapsed >= profile.ControlledReleaseAfter);
        return true;
    }

    /// <summary>
    /// Reapplies first-aid multipliers after longitudinal healing changes a wound's prototype-derived base
    /// rate. This is a bounded body-local recalculation and does not schedule a second physiology pass.
    /// </summary>
    public bool RefreshDerivedSources(EntityUid patient, TimeSpan? timestamp = null)
    {
        return TryComp<NCTreatmentStateComponent>(patient, out var state) &&
               TryGetProfile(state, out var profile) &&
               RecalculateSources(patient, state, profile, timestamp ?? _timing.CurTime);
    }

    /// <summary>
    /// Stabilizes the worst fractured bone in the selected live anatomy region. A field splint changes
    /// stability and secondary-injury risk only; fracture state and fixation requirements remain intact.
    /// </summary>
    public NCTreatmentResult SplintZone(
        EntityUid patient,
        ProtoId<NCTargetZonePrototype> zone,
        TimeSpan? timestamp = null,
        ProtoId<NCTreatmentProfilePrototype>? profileOverride = null)
    {
        var context = CreateActionContext(timestamp);
        return SplintZone(patient, zone, context, profileOverride);
    }

    public NCTreatmentResult SplintZone(
        EntityUid patient,
        ProtoId<NCTargetZonePrototype> zone,
        in NCTreatmentActionContext context,
        ProtoId<NCTreatmentProfilePrototype>? profileOverride = null)
    {
        if (!HasComp<NCMedicalBodyComponent>(patient))
            return new NCTreatmentResult(NCTreatmentOutcome.PatientNotAuthoritative, null, zone, 0f, 0f);
        if (!TryEnsureStateAndProfile(patient, profileOverride, out var state, out var profile))
            return new NCTreatmentResult(NCTreatmentOutcome.InvalidProfile, null, zone, 0f, 0f);
        if (!context.ResolutionId.IsValid)
            return new NCTreatmentResult(NCTreatmentOutcome.InvalidAction, null, zone, 0f, 0f);
        if (TryGetDuplicate(state, context, out var duplicate))
            return duplicate;

        if (!TryGetFracturedBone(patient, zone, out _, out var bone))
        {
            var noFracture = new NCTreatmentResult(NCTreatmentOutcome.NoFracture, null, zone, 0f, 0f);
            return CompleteAction(patient, state, profile, context, NCTreatmentKind.Splint, noFracture);
        }

        if (bone.Splinted)
        {
            var alreadyApplied = new NCTreatmentResult(
                NCTreatmentOutcome.AlreadyApplied,
                null,
                zone,
                bone.Stability,
                bone.Stability);
            return CompleteAction(patient, state, profile, context, NCTreatmentKind.Splint, alreadyApplied);
        }

        var previous = bone.Stability;
        bone.Splinted = true;
        bone.SplintedAt = context.Timestamp;
        bone.Stability = MathF.Max(bone.Stability, profile.SplintMinimumStability);
        bone.SecondaryDamageRisk = Math.Clamp(
            bone.SecondaryDamageRisk * profile.SplintSecondaryDamageRiskMultiplier,
            0f,
            1f);

        var result = new NCTreatmentResult(NCTreatmentOutcome.Applied, null, zone, previous, bone.Stability);
        CompleteAction(patient, state, profile, context, NCTreatmentKind.Splint, result);
        MarkChanged(patient, context.ResolutionId, NCTreatmentKind.Splint, null, zone, previous, bone.Stability);
        return result;
    }

    /// <summary>
    /// Bounded gameplay query used to avoid starting a splint DoAfter for an intact region.
    /// </summary>
    public bool CanSplintZone(EntityUid patient, ProtoId<NCTargetZonePrototype> zone)
    {
        return TryGetFracturedBone(patient, zone, out _, out var bone) && !bone.Splinted;
    }

    /// <summary>
    /// Adapter-ready item entry point. It contains no interaction validation; a caller must first perform
    /// the engine's normal range, hand and DoAfter checks, then pass the server-observed patient/wound.
    /// </summary>
    public NCTreatmentResult ApplyFromItem(
        EntityUid item,
        EntityUid patient,
        Guid woundId,
        TimeSpan? timestamp = null,
        EntityUid? actor = null)
    {
        if (!TryComp<NCTreatmentItemComponent>(item, out var treatmentItem))
            return new NCTreatmentResult(NCTreatmentOutcome.UnsupportedItem, woundId, null, 0f, 0f);

        var context = CreateActionContext(timestamp, actor, item, treatmentItem.Capability);
        return ApplyFromItem(item, patient, woundId, context);
    }

    /// <summary>
    /// Gameplay adapter entry point that preserves the DoAfter resolution ID for retry idempotency.
    /// </summary>
    public NCTreatmentResult ApplyFromItem(
        EntityUid item,
        EntityUid patient,
        Guid woundId,
        in NCTreatmentActionContext context)
    {
        if (!TryComp<NCTreatmentItemComponent>(item, out var treatmentItem) ||
            !_prototypes.TryIndex(treatmentItem.Capability, out var capability))
        {
            return new NCTreatmentResult(NCTreatmentOutcome.UnsupportedItem, woundId, null, 0f, 0f);
        }

        var enriched = context with { Capability = treatmentItem.Capability };
        return capability.Kind switch
        {
            NCTreatmentKind.Pressure => ApplyPressure(patient, woundId, enriched, capability.Profile),
            NCTreatmentKind.Bandage => BandageWound(patient, woundId, enriched, capability.Profile),
            NCTreatmentKind.Hemostatic => ApplyHemostatic(patient, woundId, enriched, capability.Profile),
            NCTreatmentKind.Tourniquet when TryGetWound(patient, woundId, out var wound, out _) =>
                SetTourniquet(patient, wound.TargetZone, enriched, capability.Profile),
            NCTreatmentKind.Splint when TryGetWoundRecord(patient, woundId, out var splintWound) =>
                SplintZone(patient, splintWound.TargetZone, enriched, capability.Profile),
            _ => new NCTreatmentResult(NCTreatmentOutcome.WoundNotFound, woundId, null, 0f, 0f),
        };
    }

    private NCTreatmentResult ApplyWoundTreatment(
        EntityUid patient,
        Guid woundId,
        NCTreatmentKind kind,
        NCWoundTreatmentFlags flag,
        in NCTreatmentActionContext context,
        ProtoId<NCTreatmentProfilePrototype>? profileOverride)
    {
        if (!HasComp<NCMedicalBodyComponent>(patient))
            return new NCTreatmentResult(NCTreatmentOutcome.PatientNotAuthoritative, woundId, null, 0f, 0f);

        if (!TryEnsureStateAndProfile(patient, profileOverride, out var state, out var profile))
            return new NCTreatmentResult(NCTreatmentOutcome.InvalidProfile, woundId, null, 0f, 0f);

        if (!context.ResolutionId.IsValid)
            return new NCTreatmentResult(NCTreatmentOutcome.InvalidAction, woundId, null, 0f, 0f);
        if (TryGetDuplicate(state, context, out var duplicate))
            return duplicate;
        if (!TryGetWound(patient, woundId, out var wound, out var bleeding))
        {
            var missing = new NCTreatmentResult(NCTreatmentOutcome.WoundNotFound, woundId, null, 0f, 0f);
            return CompleteAction(patient, state, profile, context, kind, missing);
        }

        var hasExternalSource = bleeding.Sources.Any(source =>
            source.WoundId == woundId && IsExternal(source.Channel) && source.BaseRate > 0f);
        if (!hasExternalSource)
        {
            var unchanged = GetWoundRate(patient, woundId);
            var noBleeding = new NCTreatmentResult(
                NCTreatmentOutcome.NoExternalBleeding,
                woundId,
                wound.TargetZone,
                unchanged,
                unchanged);
            return CompleteAction(patient, state, profile, context, kind, noBleeding);
        }

        if ((wound.TreatmentFlags & flag) != 0)
        {
            var unchanged = GetWoundRate(patient, woundId);
            var alreadyApplied = new NCTreatmentResult(
                NCTreatmentOutcome.AlreadyApplied,
                woundId,
                wound.TargetZone,
                unchanged,
                unchanged);
            return CompleteAction(patient, state, profile, context, kind, alreadyApplied);
        }

        var now = context.Timestamp;
        var previous = GetWoundRate(patient, woundId);
        wound.TreatmentFlags |= flag;
        wound.Stage = NCWoundStage.Treated;
        wound.ModifiedAt = now;

        var treatment = EnsureWoundTreatment(state, wound, now);
        treatment.AppliedFlags = wound.TreatmentFlags;
        treatment.ModifiedAt = now;
        switch (kind)
        {
            case NCTreatmentKind.Pressure:
                treatment.PressureAppliedAt = now;
                break;
            case NCTreatmentKind.Bandage:
                treatment.BandagedAt = now;
                treatment.BandageAbsorptionRemaining = profile.BandageAbsorptionCapacity;
                treatment.BandageSterility = profile.BandageSterility;
                break;
            case NCTreatmentKind.Hemostatic:
                treatment.HemostaticAppliedAt = now;
                break;
            case NCTreatmentKind.Tourniquet:
                break;
        }

        RecalculateSources(patient, state, profile, now);
        var current = GetWoundRate(patient, woundId);
        var result = new NCTreatmentResult(NCTreatmentOutcome.Applied, woundId, wound.TargetZone, previous, current);
        CompleteAction(patient, state, profile, context, kind, result);
        MarkChanged(patient, context.ResolutionId, kind, woundId, wound.TargetZone, previous, current);
        return result;
    }

    private bool TryEnsureStateAndProfile(
        EntityUid patient,
        ProtoId<NCTreatmentProfilePrototype>? overrideId,
        out NCTreatmentStateComponent state,
        out NCTreatmentProfilePrototype profile)
    {
        state = EnsureComp<NCTreatmentStateComponent>(patient);
        profile = default!;

        if (overrideId is { } selected)
        {
            if (!string.IsNullOrWhiteSpace(state.Profile.Id) && state.Profile != selected)
                return false;
            state.Profile = selected;
        }
        else if (string.IsNullOrWhiteSpace(state.Profile.Id))
        {
            var defaults = _prototypes.EnumeratePrototypes<NCTreatmentProfilePrototype>()
                .Where(candidate => candidate.DefaultForHumans)
                .OrderBy(candidate => candidate.ID, StringComparer.Ordinal)
                .ToList();
            if (defaults.Count != 1)
                return false;
            state.Profile = defaults[0].ID;
        }

        return TryGetProfile(state, out profile);
    }

    private bool TryGetProfile(
        NCTreatmentStateComponent state,
        out NCTreatmentProfilePrototype profile)
    {
        if (string.IsNullOrWhiteSpace(state.Profile.Id) ||
            !_prototypes.TryIndex(state.Profile, out profile!))
        {
            profile = default!;
            return false;
        }

        return ValidateProfile(profile);
    }

    private bool ValidateProfile(NCTreatmentProfilePrototype profile)
    {
        if (!IsMultiplierMapValid(profile.PressureMultipliers, preserveArterial: true) ||
            !IsMultiplierMapValid(profile.BandageMultipliers, preserveArterial: true) ||
            !IsMultiplierMapValid(profile.HemostaticMultipliers, preserveArterial: true) ||
            !IsMultiplierMapValid(profile.TourniquetMultipliers, preserveArterial: false) ||
            !float.IsFinite(profile.BandageAbsorptionCapacity) ||
            profile.BandageAbsorptionCapacity < 0f ||
            !float.IsFinite(profile.BandageSterility) ||
            profile.BandageSterility < 0f ||
            profile.BandageSterility > 1f ||
            !float.IsFinite(profile.BandageSterilityDecayPerSecond) ||
            profile.BandageSterilityDecayPerSecond < 0f ||
            !float.IsFinite(profile.SaturatedBandageContamination) ||
            profile.SaturatedBandageContamination < 0f ||
            profile.SaturatedBandageContamination > 1f ||
            profile.ControlledReleaseAfter <= TimeSpan.Zero ||
            profile.IschemiaRiskAfter <= TimeSpan.Zero ||
            profile.CriticalIschemiaAfter <= profile.IschemiaRiskAfter ||
            profile.ReleaseReboundDuration <= TimeSpan.Zero ||
            !float.IsFinite(profile.ReleaseReboundMultiplier) ||
            profile.ReleaseReboundMultiplier < 1f ||
            !float.IsFinite(profile.SplintMinimumStability) ||
            profile.SplintMinimumStability is < 0f or > 1f ||
            !float.IsFinite(profile.SplintSecondaryDamageRiskMultiplier) ||
            profile.SplintSecondaryDamageRiskMultiplier is < 0f or > 1f ||
            profile.ManualPressureDuration <= TimeSpan.Zero ||
            profile.ProcessedActionCapacity <= 0 ||
            profile.AuditHistoryCapacity <= 0 ||
            profile.TourniquetZoneRoots.Count == 0)
        {
            return false;
        }

        foreach (var (zone, root) in profile.TourniquetZoneRoots)
        {
            if (!_prototypes.TryIndex<NCTargetZonePrototype>(zone, out _) ||
                !_prototypes.TryIndex<NCTargetZonePrototype>(root, out _) ||
                !profile.TourniquetZoneRoots.TryGetValue(root, out var canonical) ||
                canonical != root)
            {
                return false;
            }
        }

        return true;
    }

    private NCTreatmentActionContext CreateActionContext(
        TimeSpan? timestamp,
        EntityUid? actor = null,
        EntityUid? item = null,
        ProtoId<NCTreatmentCapabilityPrototype>? capability = null)
    {
        return new NCTreatmentActionContext(
            NCActionResolutionId.New(),
            timestamp ?? _timing.CurTime,
            GetPrototypeId(actor),
            GetPrototypeId(item),
            capability);
    }

    private EntProtoId? GetPrototypeId(EntityUid? entity)
    {
        if (entity is not { } uid ||
            !TryComp(uid, out MetaDataComponent? metadata) ||
            metadata.EntityPrototype?.ID is not { } id)
        {
            return null;
        }

        return id;
    }

    private static bool TryGetDuplicate(
        NCTreatmentStateComponent state,
        in NCTreatmentActionContext context,
        out NCTreatmentResult result)
    {
        result = default;
        if (!state.ProcessedActions.Contains(context.ResolutionId))
            return false;

        var resolutionId = context.ResolutionId;
        var audit = state.AuditHistory.LastOrDefault(entry => entry.ResolutionId == resolutionId);
        result = audit == null
            ? new NCTreatmentResult(NCTreatmentOutcome.DuplicateAction, null, null, 0f, 0f)
            : new NCTreatmentResult(
                NCTreatmentOutcome.DuplicateAction,
                audit.WoundId,
                audit.Zone,
                audit.PreviousBleedRate,
                audit.CurrentBleedRate);
        return true;
    }

    private NCTreatmentResult CompleteAction(
        EntityUid patient,
        NCTreatmentStateComponent state,
        NCTreatmentProfilePrototype profile,
        in NCTreatmentActionContext context,
        NCTreatmentKind kind,
        in NCTreatmentResult result)
    {
        state.ProcessedActions.Add(context.ResolutionId);
        while (state.ProcessedActions.Count > profile.ProcessedActionCapacity)
            state.ProcessedActions.RemoveAt(0);

        state.AuditHistory.Add(new NCTreatmentAuditRecord
        {
            ResolutionId = context.ResolutionId,
            Kind = kind,
            WoundId = result.WoundId,
            Zone = result.Zone,
            ActorPrototype = context.ActorPrototype,
            ItemPrototype = context.ItemPrototype,
            Capability = context.Capability,
            Outcome = result.Outcome,
            PreviousBleedRate = result.PreviousBleedRate,
            CurrentBleedRate = result.CurrentBleedRate,
            Timestamp = context.Timestamp,
        });
        while (state.AuditHistory.Count > profile.AuditHistoryCapacity)
            state.AuditHistory.RemoveAt(0);

        // Audit persistence does not require an immediate physiology pass. Successful mutations raise
        // the normal medical-body event immediately after this call.
        _medicalBodies.TryMarkDirty(
            patient,
            NCMedicalDirtyDomains.Diagnostics | NCMedicalDirtyDomains.Persistence,
            false);
        return result;
    }

    private static bool IsMultiplierMapValid(
        IReadOnlyDictionary<NCBleedingChannel, float> multipliers,
        bool preserveArterial)
    {
        foreach (var channel in Enum.GetValues<NCBleedingChannel>())
        {
            if (!multipliers.TryGetValue(channel, out var value) ||
                !float.IsFinite(value) ||
                value < 0f ||
                value > 1f)
            {
                return false;
            }
        }

        return multipliers[NCBleedingChannel.Internal] == 1f &&
               (!preserveArterial || multipliers[NCBleedingChannel.ExternalArterial] > 0f);
    }

    private static NCWoundTreatmentRecord EnsureWoundTreatment(
        NCTreatmentStateComponent state,
        NCWoundRecord wound,
        TimeSpan now)
    {
        if (state.WoundTreatments.TryGetValue(wound.WoundId, out var treatment))
            return treatment;

        treatment = new NCWoundTreatmentRecord
        {
            WoundId = wound.WoundId,
            AppliedFlags = wound.TreatmentFlags,
            ModifiedAt = now,
        };
        state.WoundTreatments.Add(wound.WoundId, treatment);
        return treatment;
    }

    private bool RecalculateSources(
        EntityUid patient,
        NCTreatmentStateComponent state,
        NCTreatmentProfilePrototype profile,
        TimeSpan now)
    {
        if (!TryComp<NCBleedingComponent>(patient, out var bleeding))
            return false;

        TryComp<NCWoundContainerComponent>(patient, out var wounds);
        var changed = false;
        var tourniquetWounds = new Dictionary<ProtoId<NCTargetZonePrototype>, HashSet<Guid>>();

        if (wounds != null)
        {
            foreach (var (woundId, treatment) in state.WoundTreatments)
            {
                if ((treatment.AppliedFlags & NCWoundTreatmentFlags.Pressure) == 0 ||
                    now - treatment.PressureAppliedAt < profile.ManualPressureDuration)
                {
                    continue;
                }

                treatment.AppliedFlags &= ~NCWoundTreatmentFlags.Pressure;
                treatment.ModifiedAt = now;
                if (wounds.Wounds.TryGetValue(woundId, out var expiredWound))
                {
                    expiredWound.TreatmentFlags &= ~NCWoundTreatmentFlags.Pressure;
                    expiredWound.ModifiedAt = now;
                }
                changed = true;
            }

            foreach (var wound in wounds.Wounds.Values)
                wound.TreatmentFlags &= ~NCWoundTreatmentFlags.Tourniqueted;
        }

        foreach (var source in bleeding.Sources)
        {
            NCWoundRecord? wound = null;
            if (wounds != null && wounds.Wounds.TryGetValue(source.WoundId, out var resolvedWound))
                wound = resolvedWound;
            var flags = wound?.TreatmentFlags ?? NCWoundTreatmentFlags.None;
            var external = IsExternal(source.Channel);

            var pressure = external && (flags & NCWoundTreatmentFlags.Pressure) != 0;
            var bandaged = external && (flags & NCWoundTreatmentFlags.Bandaged) != 0;
            var tourniqueted = false;
            var multiplier = 1f;

            if (external)
            {
                if (pressure)
                    multiplier *= profile.PressureMultipliers[source.Channel];
                if (bandaged)
                    multiplier *= profile.BandageMultipliers[source.Channel];
                if ((flags & NCWoundTreatmentFlags.Hemostatic) != 0)
                    multiplier *= profile.HemostaticMultipliers[source.Channel];

                if (profile.TourniquetZoneRoots.TryGetValue(source.TargetZone, out var root) &&
                    state.Tourniquets.TryGetValue(root, out var tourniquet))
                {
                    if (tourniquet.Active)
                    {
                        multiplier *= profile.TourniquetMultipliers[source.Channel];
                        tourniqueted = true;
                        if (!tourniquetWounds.TryGetValue(root, out var covered))
                        {
                            covered = [];
                            tourniquetWounds.Add(root, covered);
                        }
                        covered.Add(source.WoundId);
                    }
                    else if (now < tourniquet.ReboundEndsAt)
                    {
                        multiplier *= profile.ReleaseReboundMultiplier;
                    }
                }
            }

            if (tourniqueted && wound != null)
                wound.TreatmentFlags |= NCWoundTreatmentFlags.Tourniqueted;

            var nextRate = source.Clamped || source.Cauterized ||
                           (flags & (NCWoundTreatmentFlags.Clamped | NCWoundTreatmentFlags.Cauterized)) != 0
                ? 0f
                : MathF.Max(0f, source.BaseRate * multiplier);
            changed |= source.CurrentRate != nextRate ||
                       source.PressureApplied != pressure ||
                       source.Bandaged != bandaged ||
                       source.Tourniqueted != tourniqueted;
            source.CurrentRate = nextRate;
            source.PressureApplied = pressure;
            source.Bandaged = bandaged;
            source.Tourniqueted = tourniqueted;
        }

        foreach (var (root, tourniquet) in state.Tourniquets)
        {
            if (!tourniquet.Active)
                continue;

            tourniquet.CoveredWoundIds = tourniquetWounds.TryGetValue(root, out var covered)
                ? covered.OrderBy(id => id).ToList()
                : [];
        }

        var previousExternal = bleeding.ActiveExternalRate;
        var previousInternal = bleeding.ActiveInternalRate;
        var previousArterial = bleeding.ActiveArterialRate;
        RecalculateAggregates(bleeding);
        return changed ||
               previousExternal != bleeding.ActiveExternalRate ||
               previousInternal != bleeding.ActiveInternalRate ||
               previousArterial != bleeding.ActiveArterialRate;
    }

    private static void RecalculateAggregates(NCBleedingComponent bleeding)
    {
        var external = MathF.Max(0f, bleeding.OverflowExternalRate);
        var internalRate = MathF.Max(0f, bleeding.OverflowInternalRate);
        var arterial = 0f;
        foreach (var source in bleeding.Sources)
        {
            var rate = MathF.Max(0f, source.CurrentRate);
            if (source.Channel == NCBleedingChannel.Internal)
            {
                internalRate += rate;
                continue;
            }

            external += rate;
            if (source.Channel == NCBleedingChannel.ExternalArterial)
                arterial += rate;
        }

        bleeding.ActiveExternalRate = external;
        bleeding.ActiveInternalRate = internalRate;
        bleeding.ActiveArterialRate = arterial;
    }

    private bool TryGetWound(
        EntityUid patient,
        Guid woundId,
        out NCWoundRecord wound,
        out NCBleedingComponent bleeding)
    {
        wound = default!;
        bleeding = default!;
        if (!TryComp<NCWoundContainerComponent>(patient, out var wounds) ||
            !wounds.Wounds.TryGetValue(woundId, out var resolvedWound) ||
            !TryComp<NCBleedingComponent>(patient, out var resolvedBleeding))
        {
            return false;
        }

        wound = resolvedWound;
        bleeding = resolvedBleeding;
        return true;
    }

    private bool TryGetWoundRecord(EntityUid patient, Guid woundId, out NCWoundRecord wound)
    {
        wound = default!;
        if (!TryComp<NCWoundContainerComponent>(patient, out var wounds) ||
            !wounds.Wounds.TryGetValue(woundId, out var resolved))
        {
            return false;
        }

        wound = resolved;
        return true;
    }

    private bool TryGetFracturedBone(
        EntityUid patient,
        ProtoId<NCTargetZonePrototype> zone,
        out EntityUid boneUid,
        out NCBoneComponent bone)
    {
        boneUid = default;
        bone = default!;
        if (!_anatomy.TryGetZone((patient, null), zone, out _, out var region))
            return false;

        var candidates = new List<(EntityUid Uid, NCBoneComponent Bone)>();
        if (TryComp<NCBoneComponent>(region, out var regionBone) && regionBone.State >= NCBoneState.Cracked)
            candidates.Add((region, regionBone));

        foreach (var child in _organRelations.AllChildren(region))
        {
            if (TryComp<NCBoneComponent>(child.Owner, out var childBone) && childBone.State >= NCBoneState.Cracked)
                candidates.Add((child.Owner, childBone));
        }

        if (candidates.Count == 0)
            return false;

        var selected = candidates
            .OrderBy(candidate => candidate.Bone.Splinted)
            .ThenByDescending(candidate => candidate.Bone.State)
            .ThenBy(candidate => candidate.Bone.Stability)
            .ThenBy(candidate => candidate.Uid.Id)
            .First();
        boneUid = selected.Uid;
        bone = selected.Bone;
        return true;
    }

    private float GetWoundRate(EntityUid patient, Guid woundId)
    {
        if (!TryComp<NCBleedingComponent>(patient, out var bleeding))
            return 0f;
        return bleeding.Sources
            .Where(source => source.WoundId == woundId)
            .Sum(source => MathF.Max(0f, source.CurrentRate));
    }

    private float GetLimbRate(
        EntityUid patient,
        NCTreatmentProfilePrototype profile,
        ProtoId<NCTargetZonePrototype> root)
    {
        if (!TryComp<NCBleedingComponent>(patient, out var bleeding))
            return 0f;
        return bleeding.Sources
            .Where(source => IsExternal(source.Channel) && IsInLimb(profile, source.TargetZone, root))
            .Sum(source => MathF.Max(0f, source.CurrentRate));
    }

    private void MarkChanged(
        EntityUid patient,
        NCActionResolutionId resolutionId,
        NCTreatmentKind kind,
        Guid? woundId,
        ProtoId<NCTargetZonePrototype>? zone,
        float previousRate,
        float currentRate)
    {
        _medicalBodies.TryMarkDirty(
            patient,
            NCMedicalDirtyDomains.Wounds |
            NCMedicalDirtyDomains.Vascular |
            NCMedicalDirtyDomains.Pain |
            NCMedicalDirtyDomains.Consciousness |
            NCMedicalDirtyDomains.Diagnostics |
            NCMedicalDirtyDomains.Persistence);

        var changed = new NCTreatmentChangedEvent(
            resolutionId,
            kind,
            woundId,
            zone,
            previousRate,
            currentRate);
        RaiseLocalEvent(patient, ref changed);
    }

    private static bool IsExternal(NCBleedingChannel channel)
    {
        return channel != NCBleedingChannel.Internal;
    }

    private static bool IsInLimb(
        NCTreatmentProfilePrototype profile,
        ProtoId<NCTargetZonePrototype> zone,
        ProtoId<NCTargetZonePrototype> root)
    {
        return profile.TourniquetZoneRoots.TryGetValue(zone, out var candidate) && candidate == root;
    }
}
