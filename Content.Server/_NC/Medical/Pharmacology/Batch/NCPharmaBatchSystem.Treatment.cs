// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Pharmacology.Batch;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

public enum NCPharmaTreatment : byte
{
    /// <summary>Sterile cabinet: raises sterility, the heat costs some stability.</summary>
    Sterilize,

    /// <summary>Formulation station: raises homogeneity, the extra handling costs some stability.</summary>
    Homogenize,
}

/// <summary>Treatment of finished batches and the packs they sit in.</summary>
public sealed partial class NCPharmaBatchSystem
{
    /// <summary>
    /// Treats a batch as a whole: a batch is one production lot, so treating one pack of it treats the lot.
    /// The gain closes part of the gap to the quality ceiling; the stability cost is what the treatment pays.
    /// </summary>
    public bool TryTreatBatch(
        Guid batchId,
        NCPharmaTreatment treatment,
        float gapClosed,
        float stabilityCost,
        float quality,
        string actor)
    {
        if (!_batches.TryGetValue(batchId, out var batch) || !TryGetTuning(out var tuning))
            return false;

        var gain = Math.Clamp(gapClosed * quality, 0f, 1f);
        var actual = batch.Actual;
        switch (treatment)
        {
            case NCPharmaTreatment.Sterilize:
                actual = actual with
                {
                    Sterility = Raise(actual.Sterility, gain),
                    Stability = Math.Max(0f, actual.Stability - stabilityCost),
                };
                break;
            default:
                actual = actual with
                {
                    Homogeneity = Raise(actual.Homogeneity, gain),
                    Stability = Math.Max(0f, actual.Stability - stabilityCost),
                };
                break;
        }

        batch.Actual = actual;
        AddAudit(batch, tuning, NCPharmaAuditKind.Treated, actor,
            $"{treatment}: sterility {actual.Sterility:P1}, homogeneity {actual.Homogeneity:P1}, stability {actual.Stability:P1}");
        RefreshContainers(batch);
        return true;
    }

    private static float Raise(float value, float gapShare) =>
        value + MathF.Max(0f, NCPharmaBatchMath.QualityCeiling - value) * gapShare;

    /// <summary>The pack prototype a formula is delivered in: its form's own pack, or the given fallback.</summary>
    public EntProtoId ResolvePack(string formulaId, EntProtoId fallback)
    {
        return TryGetFormula(formulaId, out var formula) &&
               _prototypes.TryIndex(formula.DeliveryForm, out var form) &&
               form.Pack is { } pack
            ? pack
            : fallback;
    }

    /// <summary>
    /// Splits the doses of a pack into new packs of the given size in the form's own pack. The doses stay in
    /// the same batch; only the physical packaging changes.
    /// </summary>
    public int TryRepack(
        EntityUid source,
        int dosesPerPack,
        EntityCoordinates coordinates,
        string actor)
    {
        if (dosesPerPack < 1 ||
            !TryComp<NCPharmaContainerComponent>(source, out var pack) ||
            pack.Doses < 1 ||
            !_batches.TryGetValue(pack.BatchId, out var batch))
        {
            return 0;
        }

        var prototype = ResolvePack(batch.Formula, MetaData(source).EntityPrototype?.ID ?? "NCPharmaAutoinjector");
        var total = pack.Doses;
        // Release the doses first so the new packs may claim them from the batch again.
        batch.DosesPacked -= total;
        var remaining = total;
        var packs = 0;
        while (remaining > 0)
        {
            var doses = Math.Min(dosesPerPack, remaining);
            if (TrySpawnContainer(prototype, coordinates, batch.BatchId, doses, actor) == null)
                break;

            remaining -= doses;
            packs++;
        }

        if (remaining > 0)
        {
            // Something went wrong: keep the original pack with what could not be moved.
            batch.DosesPacked += remaining;
            pack.Doses = remaining;
            Dirty(source, pack);
            return packs;
        }

        QueueDel(source);
        return packs;
    }
}
