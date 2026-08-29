// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.ActionResolution;
using Content.Server._NC.Medical.Pharmacology;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Wounds;
using Content.Shared.Body;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Bleeding;

/// <summary>
/// Advances all bleeding sources for one scheduled body in a single bounded pass.
/// It never scans the world and never owns an Update loop.
/// </summary>
public sealed partial class NCBleedingSystem : EntitySystem
{
    [Dependency] private NCMedicalBodySystem _medicalBodies = default!;
    [Dependency] private NCCirculationBloodSystem _blood = default!;
    [Dependency] private NCPharmacologySystem _pharmacology = default!;

    /// <summary>
    /// Creates or updates the external and internal sources produced by one detailed wound.
    /// Treatment systems will change CurrentRate while preserving the prototype-derived BaseRate.
    /// </summary>
    public void SynchronizeWoundSources(EntityUid body, NCWoundRecord wound)
    {
        var bleeding = EnsureComp<NCBleedingComponent>(body);
        var externalChannel = wound.PrimaryBleedKind switch
        {
            NCBleedKind.Arterial => NCBleedingChannel.ExternalArterial,
            NCBleedKind.Venous => NCBleedingChannel.ExternalVenous,
            NCBleedKind.Capillary => NCBleedingChannel.ExternalCapillary,
            _ => NCBleedingChannel.ExternalCapillary,
        };

        // Rebuild this wound's at-most-two sources atomically. A merge can change both the external channel
        // and internal rate; replacing the tiny bounded subset prevents stale or duplicate contributions.
        bleeding.Sources.RemoveAll(source => source.WoundId == wound.WoundId);

        UpsertSource(bleeding, wound, externalChannel, wound.ExternalBleedRate);
        UpsertSource(bleeding, wound, NCBleedingChannel.Internal, wound.InternalBleedRate);
        RecalculateRates(bleeding);
    }

    public void AddOverflow(
        EntityUid body,
        float externalRate,
        float internalRate,
        ProtoId<OrganCategoryPrototype> regionCategory)
    {
        var bleeding = EnsureComp<NCBleedingComponent>(body);
        bleeding.OverflowExternalRate = MathF.Max(0f, bleeding.OverflowExternalRate + externalRate);
        bleeding.OverflowInternalRate = MathF.Max(0f, bleeding.OverflowInternalRate + internalRate);
        if (internalRate > 0f)
        {
            bleeding.OverflowInternalRatesByRegion[regionCategory] =
                bleeding.OverflowInternalRatesByRegion.GetValueOrDefault(regionCategory) + internalRate;
        }
        RecalculateRates(bleeding);
    }

    /// <summary>
    /// Integrates blood loss over scheduler time. Returns the actually removed blood volume.
    /// </summary>
    public float AdvanceBody(EntityUid body, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero ||
            !TryComp<NCBleedingComponent>(body, out var bleeding))
        {
            return 0f;
        }

        RecalculateRates(bleeding);
        var seconds = (float) elapsed.TotalSeconds;
        var configuredBleedingMultiplier = _pharmacology.GetModifiers(body).BleedingMultiplier;
        var bleedingMultiplier = float.IsFinite(configuredBleedingMultiplier)
            ? MathF.Max(0f, configuredBleedingMultiplier)
            : 1f;
        var requestedExternal = MathF.Max(0f, bleeding.ActiveExternalRate * seconds * bleedingMultiplier);
        var requestedInternalByRegion = BuildInternalLossByRegion(bleeding, seconds, out var attributedOverflow);
        if (bleedingMultiplier != 1f)
        {
            foreach (var region in requestedInternalByRegion.Keys.ToArray())
                requestedInternalByRegion[region] *= bleedingMultiplier;
            attributedOverflow *= bleedingMultiplier;
        }
        var requestedUnattributedInternal = MathF.Max(
            0f,
            bleeding.OverflowInternalRate * seconds * bleedingMultiplier - attributedOverflow);
        if (requestedExternal <= 0f &&
            requestedInternalByRegion.Count == 0 &&
            requestedUnattributedInternal <= 0f)
        {
            return 0f;
        }

        if (!_blood.TryApplyBleedingLoss(
                body,
                requestedExternal,
                requestedInternalByRegion,
                requestedUnattributedInternal,
                out var application) ||
            application.TotalLost <= 0f)
        {
            return 0f;
        }

        bleeding.InternalBloodLoss += application.InternalLost;
        bleeding.UnlocalizedInternalBloodVolume += application.UnlocalizedInternalLost;

        // The scheduler suppresses a body's own due-now requeue while processing, but persistence must
        // still observe every authoritative volume mutation (including aggregate overflow bleeding).
        _medicalBodies.TryMarkDirty(
            body,
            NCMedicalDirtyDomains.Vascular |
            NCMedicalDirtyDomains.Pain |
            NCMedicalDirtyDomains.OrganFunction |
            NCMedicalDirtyDomains.Consciousness |
            NCMedicalDirtyDomains.Diagnostics |
            NCMedicalDirtyDomains.Persistence,
            true);

        var changed = new NCBleedingChangedEvent(
            application.TotalLost,
            application.ExternalLost,
            application.InternalLost,
            bleeding.ActiveExternalRate,
            bleeding.ActiveInternalRate,
            bleeding.ActiveArterialRate);
        RaiseLocalEvent(body, ref changed);
        return application.TotalLost;
    }

    /// <summary>
    /// Applies the one-time external pulse produced by an accepted open impact. It uses the same authoritative
    /// circulation-to-bloodstream bridge as ongoing bleeding, so the visual response cannot create free blood.
    /// </summary>
    public float ApplyExternalPulse(EntityUid body, float requestedVolume)
    {
        if (!float.IsFinite(requestedVolume) || requestedVolume <= 0f ||
            !_blood.TryApplyBleedingLoss(
                body,
                requestedVolume,
                new Dictionary<ProtoId<OrganCategoryPrototype>, float>(),
                0f,
                out var application) ||
            application.ExternalLost <= 0f)
        {
            return 0f;
        }

        var bleeding = EnsureComp<NCBleedingComponent>(body);
        _medicalBodies.TryMarkDirty(
            body,
            NCMedicalDirtyDomains.Vascular |
            NCMedicalDirtyDomains.Pain |
            NCMedicalDirtyDomains.OrganFunction |
            NCMedicalDirtyDomains.Consciousness |
            NCMedicalDirtyDomains.Diagnostics |
            NCMedicalDirtyDomains.Persistence,
            true);

        var changed = new NCBleedingChangedEvent(
            application.TotalLost,
            application.ExternalLost,
            0f,
            bleeding.ActiveExternalRate,
            bleeding.ActiveInternalRate,
            bleeding.ActiveArterialRate);
        RaiseLocalEvent(body, ref changed);
        return application.ExternalLost;
    }

    public bool HasActiveBleeding(EntityUid body)
    {
        return TryComp<NCBleedingComponent>(body, out var bleeding) &&
               bleeding.ActiveExternalRate + bleeding.ActiveInternalRate > 0f;
    }

    public void ResetBody(EntityUid body)
    {
        var bleeding = EnsureComp<NCBleedingComponent>(body);
        bleeding.Sources.Clear();
        bleeding.OverflowExternalRate = 0f;
        bleeding.OverflowInternalRate = 0f;
        bleeding.OverflowInternalRatesByRegion.Clear();
        bleeding.InternalBloodLoss = 0f;
        bleeding.UnlocalizedInternalBloodVolume = 0f;
        RecalculateRates(bleeding);
    }

    /// <summary>
    /// Removes the derived sources owned by a set of stable wound IDs and returns the same records for
    /// persistence on a detached organ root. No entity identity is stored in those records.
    /// </summary>
    public List<NCBleedingSourceRecord> ExtractWoundSources(
        EntityUid body,
        IReadOnlySet<Guid> woundIds)
    {
        if (woundIds.Count == 0 || !TryComp<NCBleedingComponent>(body, out var bleeding))
            return [];

        var extracted = bleeding.Sources
            .Where(source => woundIds.Contains(source.WoundId))
            .ToList();
        if (extracted.Count == 0)
            return extracted;

        bleeding.Sources.RemoveAll(source => woundIds.Contains(source.WoundId));
        RecalculateRates(bleeding);
        return extracted;
    }

    /// <summary>
    /// Removes all derived channels for wounds that completed longitudinal resolution. Scar records keep
    /// the permanent history; inactive bleeding sources must not consume the live source budget.
    /// </summary>
    public int RemoveWoundSources(EntityUid body, IReadOnlySet<Guid> woundIds)
    {
        if (woundIds.Count == 0 || !TryComp<NCBleedingComponent>(body, out var bleeding))
            return 0;

        var removed = bleeding.Sources.RemoveAll(source => woundIds.Contains(source.WoundId));
        if (removed > 0)
            RecalculateRates(bleeding);
        return removed;
    }

    /// <summary>
    /// Restores previously extracted, persistence-safe sources to an authoritative body. Duplicate source
    /// IDs are rejected so a repeated attachment cannot amplify bleeding.
    /// </summary>
    public bool RestoreWoundSources(EntityUid body, IReadOnlyList<NCBleedingSourceRecord> sources)
    {
        if (sources.Count == 0)
            return true;

        var bleeding = EnsureComp<NCBleedingComponent>(body);
        var existing = bleeding.Sources.Select(source => source.SourceId).ToHashSet();
        if (sources.Any(source => source.SourceId == Guid.Empty || !existing.Add(source.SourceId)))
            return false;

        bleeding.Sources.AddRange(sources);
        RecalculateRates(bleeding);
        return true;
    }

    /// <summary>
    /// Moves a deterministic fraction of legacy aggregate overflow bleeding to a detached payload. The
    /// current wound model stores overflow trauma per zone but its derived bleed fallback is aggregate, so
    /// the trauma fraction is the only stable attribution available until that fallback is migrated.
    /// </summary>
    public NCOverflowBleedingExtraction ExtractOverflowFraction(
        EntityUid body,
        float fraction,
        IReadOnlySet<ProtoId<OrganCategoryPrototype>> attributedRegions)
    {
        if (!TryComp<NCBleedingComponent>(body, out var bleeding))
            return default;

        fraction = Math.Clamp(fraction, 0f, 1f);
        var external = MathF.Max(0f, bleeding.OverflowExternalRate) * fraction;
        var aggregateInternal = MathF.Max(0f, bleeding.OverflowInternalRate);
        var mappedInternal = bleeding.OverflowInternalRatesByRegion.Values
            .Sum(rate => MathF.Max(0f, rate));
        var unattributedInternal = MathF.Max(0f, aggregateInternal - mappedInternal);
        bleeding.OverflowExternalRate = MathF.Max(0f, bleeding.OverflowExternalRate - external);
        var internalByRegion = new Dictionary<ProtoId<OrganCategoryPrototype>, float>();
        foreach (var (region, rate) in bleeding.OverflowInternalRatesByRegion.ToArray())
        {
            if (!attributedRegions.Contains(region))
                continue;

            var moved = MathF.Max(0f, rate);
            if (moved > 0f)
                internalByRegion.Add(region, moved);
            bleeding.OverflowInternalRatesByRegion.Remove(region);
        }
        var internalRate = MathF.Min(
            aggregateInternal,
            internalByRegion.Values.Sum() + unattributedInternal * fraction);
        bleeding.OverflowInternalRate = MathF.Max(0f, aggregateInternal - internalRate);
        RecalculateRates(bleeding);
        return new NCOverflowBleedingExtraction(external, internalRate, internalByRegion);
    }

    public void RestoreOverflow(
        EntityUid body,
        float external,
        float internalRate,
        IReadOnlyDictionary<ProtoId<OrganCategoryPrototype>, float>? internalByRegion = null)
    {
        if (external <= 0f && internalRate <= 0f)
            return;

        var bleeding = EnsureComp<NCBleedingComponent>(body);
        bleeding.OverflowExternalRate += MathF.Max(0f, external);
        bleeding.OverflowInternalRate += MathF.Max(0f, internalRate);
        if (internalByRegion != null)
        {
            foreach (var (region, rate) in internalByRegion)
            {
                if (!float.IsFinite(rate) || rate <= 0f)
                    continue;

                bleeding.OverflowInternalRatesByRegion[region] =
                    bleeding.OverflowInternalRatesByRegion.GetValueOrDefault(region) + rate;
            }
        }
        RecalculateRates(bleeding);
    }

    private static Dictionary<ProtoId<OrganCategoryPrototype>, float> BuildInternalLossByRegion(
        NCBleedingComponent bleeding,
        float seconds,
        out float attributedOverflow)
    {
        var result = new Dictionary<ProtoId<OrganCategoryPrototype>, float>();
        foreach (var source in bleeding.Sources)
        {
            if (source.Channel != NCBleedingChannel.Internal)
                continue;

            var amount = MathF.Max(0f, source.CurrentRate) * seconds;
            if (amount > 0f)
                result[source.RegionCategory] = result.GetValueOrDefault(source.RegionCategory) + amount;
        }

        attributedOverflow = 0f;
        var rawOverflowTotal = bleeding.OverflowInternalRatesByRegion.Values
            .Sum(rate => MathF.Max(0f, rate));
        var aggregateOverflow = MathF.Max(0f, bleeding.OverflowInternalRate);
        var attributionScale = rawOverflowTotal > aggregateOverflow && rawOverflowTotal > 0f
            ? aggregateOverflow / rawOverflowTotal
            : 1f;
        foreach (var (region, rate) in bleeding.OverflowInternalRatesByRegion)
        {
            var amount = MathF.Max(0f, rate) * attributionScale * seconds;
            if (amount <= 0f)
                continue;

            result[region] = result.GetValueOrDefault(region) + amount;
            attributedOverflow += amount;
        }

        return result;
    }

    private static void UpsertSource(
        NCBleedingComponent bleeding,
        NCWoundRecord wound,
        NCBleedingChannel channel,
        float rate)
    {
        var existing = bleeding.Sources.Find(source =>
            source.WoundId == wound.WoundId && source.Channel == channel);

        if (rate <= 0f)
        {
            if (existing != null)
                bleeding.Sources.Remove(existing);
            return;
        }

        if (existing == null)
        {
            existing = new NCBleedingSourceRecord
            {
                // One wound/channel pair has one stable derived source identity across replay and persistence.
                SourceId = new NCActionResolutionId(wound.WoundId).DeriveGuid((uint) channel),
                WoundId = wound.WoundId,
                TargetZone = wound.TargetZone,
                RegionCategory = wound.RegionCategory,
                Channel = channel,
            };
            bleeding.Sources.Add(existing);
        }

        existing.BaseRate = rate;
        existing.CurrentRate = rate;
    }

    private static void RecalculateRates(NCBleedingComponent bleeding)
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
}

public readonly record struct NCOverflowBleedingExtraction(
    float External,
    float Internal,
    IReadOnlyDictionary<ProtoId<OrganCategoryPrototype>, float> InternalByRegion);
