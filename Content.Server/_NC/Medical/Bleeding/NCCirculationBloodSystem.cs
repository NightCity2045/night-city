// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared.Body;
using Content.Shared.Body.Components;
using Content.Shared.Body.Systems;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Bleeding;

/// <summary>
/// Owns the boundary between authoritative NC circulation and the upstream bloodstream solution. The
/// circulation scalar remains the source of truth; the solution remains the physical carrier for blood,
/// medicines, toxins and metabolites and is changed through upstream APIs only.
/// </summary>
public sealed partial class NCCirculationBloodSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedBloodstreamSystem _bloodstream = default!;
    [Dependency] private SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private NCAnatomySystem _anatomy = default!;
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;

    /// <summary>
    /// Applies one scheduler interval of external and cavity-attributed internal blood loss. External loss
    /// uses TryBleedOut so upstream creates physical blood; internal loss removes solution without spilling.
    /// </summary>
    public bool TryApplyBleedingLoss(
        EntityUid body,
        float requestedExternal,
        IReadOnlyDictionary<ProtoId<OrganCategoryPrototype>, float> requestedInternalByRegion,
        float requestedUnattributedInternal,
        out NCBloodLossApplication result)
    {
        result = default;
        requestedExternal = PositiveFinite(requestedExternal);
        requestedUnattributedInternal = PositiveFinite(requestedUnattributedInternal);

        if (!TryGetContext(body, out var circulation, out var profile, out var upstream))
            return false;

        var cavityRequests = new Dictionary<EntityUid, float>();
        var requestedInternal = requestedUnattributedInternal;
        foreach (var (category, rawAmount) in requestedInternalByRegion)
        {
            var amount = PositiveFinite(rawAmount);
            if (amount <= 0f)
                continue;

            requestedInternal += amount;
            if (TryResolveCavity(body, category, out var cavity))
                cavityRequests[cavity] = cavityRequests.GetValueOrDefault(cavity) + amount;
        }

        if (requestedExternal <= 0f && requestedInternal <= 0f)
            return true;

        var available = MathF.Max(0f, circulation.BloodVolume);
        var requestedTotal = requestedExternal + requestedInternal;
        var scale = requestedTotal > available && requestedTotal > 0f
            ? available / requestedTotal
            : 1f;
        var external = requestedExternal * scale;
        var internalAmount = requestedInternal * scale;
        var actualTotal = external + internalAmount;
        if (actualTotal <= 0f)
            return true;

        // Resolve both upstream solutions before the first mutation so a malformed human body fails closed
        // instead of losing authoritative volume without a matching physical solution mutation.
        if (!_solutions.ResolveSolution(body, upstream.BloodSolutionName, ref upstream.BloodSolution) ||
            external > 0f &&
            !_solutions.ResolveSolution(body, upstream.BloodTemporarySolutionName, ref upstream.TemporarySolution))
        {
            return false;
        }

        var solutionExternal = ToSolutionVolume(external, profile, upstream);
        var solutionInternal = ToSolutionVolume(internalAmount, profile, upstream);
        if (solutionExternal > FixedPoint2.Zero &&
            !_bloodstream.TryBleedOut((body, upstream), solutionExternal))
        {
            return false;
        }

        if (solutionInternal > FixedPoint2.Zero &&
            !_bloodstream.TryModifyBloodLevel((body, upstream), -solutionInternal))
        {
            return false;
        }

        var previous = circulation.BloodVolume;
        circulation.BloodVolume = Math.Clamp(previous - actualTotal, 0f, previous);
        circulation.EffectiveCirculatingVolume = Math.Min(
            circulation.EffectiveCirculatingVolume,
            circulation.BloodVolume);

        // FixedPoint2 rounds physical transfers; converge the blood reagents to the authoritative scalar
        // while preserving non-blood reagents already carried by the upstream solution.
        if (!SynchronizeSolution(body, circulation, profile, upstream))
            return false;

        foreach (var (cavityUid, requested) in cavityRequests)
            AccumulateCavity(cavityUid, requested * scale);

        // Legacy snapshots can contain aggregate internal overflow without region attribution. Keep it
        // physical and authoritative, but never invent a cavity assignment for unknown historical data.
        result = new NCBloodLossApplication(
            previous - circulation.BloodVolume,
            external,
            internalAmount);
        return true;
    }

    /// <summary>
    /// Applies a compatible whole-blood transfusion and mirrors it into upstream blood reagents as one
    /// authoritative operation. Compatibility is supplied by the data-defined emergency profile.
    /// </summary>
    public NCBloodTransfusionApplication TryTransfuse(
        EntityUid body,
        string donorCompatibility,
        float requestedVolume,
        float maximumVolumeMultiplier,
        IReadOnlyDictionary<string, List<string>> compatibility,
        bool markDirty = true)
    {
        if (!TryGetContext(body, out var circulation, out var profile, out var upstream))
            return new NCBloodTransfusionApplication(NCBloodTransfusionOutcome.InvalidPatient, 0f, 0f);

        if (string.IsNullOrWhiteSpace(donorCompatibility) ||
            string.IsNullOrWhiteSpace(circulation.TransfusionCompatibility) ||
            !compatibility.TryGetValue(donorCompatibility, out var recipients) ||
            !recipients.Contains(circulation.TransfusionCompatibility, StringComparer.Ordinal))
        {
            return new NCBloodTransfusionApplication(
                NCBloodTransfusionOutcome.Incompatible,
                circulation.BloodVolume,
                circulation.BloodVolume);
        }

        if (!float.IsFinite(requestedVolume) || requestedVolume <= 0f ||
            !float.IsFinite(maximumVolumeMultiplier) || maximumVolumeMultiplier <= 0f)
        {
            return new NCBloodTransfusionApplication(
                NCBloodTransfusionOutcome.InvalidVolume,
                circulation.BloodVolume,
                circulation.BloodVolume);
        }

        var maximum = profile.NominalBloodVolume * maximumVolumeMultiplier;
        var previous = Math.Clamp(circulation.BloodVolume, 0f, maximum);
        var delivered = Math.Clamp(requestedVolume, 0f, maximum - previous);
        if (delivered <= 0f)
        {
            return new NCBloodTransfusionApplication(
                NCBloodTransfusionOutcome.AtCapacity,
                previous,
                previous);
        }

        if (!_solutions.ResolveSolution(body, upstream.BloodSolutionName, ref upstream.BloodSolution))
        {
            return new NCBloodTransfusionApplication(
                NCBloodTransfusionOutcome.InvalidPatient,
                previous,
                previous);
        }

        circulation.BloodVolume = previous + delivered;
        if (!SynchronizeSolution(body, circulation, profile, upstream))
        {
            circulation.BloodVolume = previous;
            SynchronizeSolution(body, circulation, profile, upstream);
            return new NCBloodTransfusionApplication(
                NCBloodTransfusionOutcome.InvalidPatient,
                previous,
                previous);
        }

        circulation.EffectiveCirculatingVolume = MathF.Max(
            circulation.EffectiveCirculatingVolume,
            circulation.BloodVolume);
        if (markDirty)
        {
            _medicalBodies.TryMarkDirty(
                body,
                NCMedicalDirtyDomains.Vascular |
                NCMedicalDirtyDomains.OrganFunction |
                NCMedicalDirtyDomains.Consciousness |
                NCMedicalDirtyDomains.Diagnostics |
                NCMedicalDirtyDomains.Persistence);
        }
        return new NCBloodTransfusionApplication(
            NCBloodTransfusionOutcome.Applied,
            previous,
            circulation.BloodVolume);
    }

    /// <summary>
    /// Reconciles upstream blood reagents after persistence restore or an authorized full reset.
    /// </summary>
    public bool SynchronizeSolution(EntityUid body)
    {
        return TryGetContext(body, out var circulation, out var profile, out var upstream) &&
               SynchronizeSolution(body, circulation, profile, upstream);
    }

    /// <summary>
    /// Clears retained cavity blood during the trusted rejuvenation boundary.
    /// </summary>
    public void ResetCavities(EntityUid body)
    {
        if (!TryComp<BodyComponent>(body, out var bodyComponent) || bodyComponent.Organs == null)
            return;

        foreach (var organ in bodyComponent.Organs.ContainedEntities)
        {
            if (!TryComp<NCCavityFluidComponent>(organ, out var cavity))
                continue;

            var previous = cavity.BloodVolume;
            cavity.BloodVolume = 0f;
            cavity.DrainRate = 0f;
            cavity.Pressure = 0f;
            cavity.OrganCompression = 0f;
            if (previous <= 0f)
                continue;

            var changed = new NCCavityFluidChangedEvent(cavity.Cavity, previous, 0f);
            RaiseLocalEvent(organ, ref changed);
        }
    }

    private bool TryGetContext(
        EntityUid body,
        out NCCirculationComponent circulation,
        out NCPhysiologyProfilePrototype profile,
        out BloodstreamComponent upstream)
    {
        circulation = default!;
        profile = default!;
        upstream = default!;
        if (!HasComp<NCMedicalBodyComponent>(body) ||
            !TryComp<NCCirculationComponent>(body, out var resolvedCirculation) ||
            !resolvedCirculation.StateInitialized ||
            !_prototypes.TryIndex(resolvedCirculation.Profile, out var resolvedProfile) ||
            resolvedProfile.NominalBloodVolume <= 0f ||
            !float.IsFinite(resolvedProfile.NominalBloodVolume) ||
            !TryComp<BloodstreamComponent>(body, out var resolvedUpstream) ||
            resolvedUpstream.BloodReferenceSolution.Volume <= FixedPoint2.Zero)
        {
            return false;
        }

        circulation = resolvedCirculation;
        profile = resolvedProfile;
        upstream = resolvedUpstream;
        return true;
    }

    private bool TryResolveCavity(
        EntityUid body,
        ProtoId<OrganCategoryPrototype> category,
        out EntityUid cavityUid)
    {
        cavityUid = default;
        if (!_anatomy.TryGetOrgan(body, category, out var region) ||
            !TryComp<NCVascularRegionComponent>(region, out var vascular) ||
            vascular.Cavity == NCBodyCavity.None ||
            !TryComp<NCCavityFluidComponent>(region, out var cavity) ||
            cavity.Cavity != vascular.Cavity ||
            !ValidCavity(cavity))
        {
            return false;
        }

        cavityUid = region;
        return true;
    }

    private void AccumulateCavity(EntityUid cavityUid, float amount)
    {
        if (amount <= 0f || !TryComp<NCCavityFluidComponent>(cavityUid, out var cavity))
            return;

        var previous = Math.Clamp(cavity.BloodVolume, 0f, cavity.MaximumBloodVolume);
        cavity.BloodVolume = Math.Clamp(previous + amount, 0f, cavity.MaximumBloodVolume);
        cavity.Pressure = Math.Clamp(cavity.BloodVolume / cavity.PressureFullScaleVolume, 0f, 1f);
        cavity.OrganCompression = Math.Clamp(cavity.BloodVolume / cavity.CompressionFullScaleVolume, 0f, 1f);
        if (cavity.BloodVolume == previous)
            return;

        var changed = new NCCavityFluidChangedEvent(cavity.Cavity, previous, cavity.BloodVolume);
        RaiseLocalEvent(cavityUid, ref changed);
    }

    private bool SynchronizeSolution(
        EntityUid body,
        NCCirculationComponent circulation,
        NCPhysiologyProfilePrototype profile,
        BloodstreamComponent upstream)
    {
        var targetFraction = Math.Clamp(
            circulation.BloodVolume / profile.NominalBloodVolume,
            0f,
            upstream.MaxVolumeModifier);
        var maximumAdjustment = upstream.BloodReferenceSolution.Volume * upstream.MaxVolumeModifier;
        return maximumAdjustment > FixedPoint2.Zero &&
               _bloodstream.TryRegulateBloodLevel(
                   (body, upstream),
                   maximumAdjustment,
                   targetFraction);
    }

    private static FixedPoint2 ToSolutionVolume(
        float medicalVolume,
        NCPhysiologyProfilePrototype profile,
        BloodstreamComponent upstream)
    {
        var ratio = (float) upstream.BloodReferenceSolution.Volume / profile.NominalBloodVolume;
        return FixedPoint2.New(MathF.Max(0f, medicalVolume * ratio));
    }

    private static bool ValidCavity(NCCavityFluidComponent cavity)
    {
        return cavity.Cavity != NCBodyCavity.None &&
               float.IsFinite(cavity.MaximumBloodVolume) && cavity.MaximumBloodVolume > 0f &&
               float.IsFinite(cavity.PressureFullScaleVolume) && cavity.PressureFullScaleVolume > 0f &&
               float.IsFinite(cavity.CompressionFullScaleVolume) && cavity.CompressionFullScaleVolume > 0f;
    }

    private static float PositiveFinite(float value)
    {
        return float.IsFinite(value) ? MathF.Max(0f, value) : 0f;
    }
}

public readonly record struct NCBloodLossApplication(
    float TotalLost,
    float ExternalLost,
    float InternalLost);

public enum NCBloodTransfusionOutcome : byte
{
    Applied,
    InvalidPatient,
    InvalidVolume,
    Incompatible,
    AtCapacity,
}

public readonly record struct NCBloodTransfusionApplication(
    NCBloodTransfusionOutcome Outcome,
    float PreviousVolume,
    float BloodVolume);
