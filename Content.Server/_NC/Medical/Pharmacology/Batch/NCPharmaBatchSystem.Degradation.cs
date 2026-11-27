// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Pharmacology.Batch;
using Content.Shared.Power.EntitySystems;
using Robust.Shared.Containers;

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Cold chain. An unstable batch loses purity while any pack of it is outside a working cold storage. Stable
/// batches are never touched, so ordinary medicine needs no attention and only rare unstable ones make a task
/// of cooling and fast delivery. There is no timer on any single dose: the batch ages as a whole.
/// </summary>
public sealed partial class NCPharmaBatchSystem
{
    private const int MaximumContainerDepth = 8;
    private const float AuditPurityStep = 0.05f;

    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedPowerReceiverSystem _power = default!;

    private float _degradationTimer;
    private readonly HashSet<Guid> _exposed = [];

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_batches.Count == 0 || !TryGetTuning(out var tuning))
            return;

        _degradationTimer += frameTime;
        var interval = (float) tuning.DegradationInterval.TotalSeconds;
        if (interval <= 0f || _degradationTimer < interval)
            return;

        var seconds = _degradationTimer;
        _degradationTimer = 0f;
        AdvanceDegradation(tuning, seconds);
    }

    private void AdvanceDegradation(NCPharmaTuningPrototype tuning, float seconds)
    {
        _exposed.Clear();
        var query = EntityQueryEnumerator<NCPharmaContainerComponent>();
        while (query.MoveNext(out var uid, out var container))
        {
            // Each pack keeps its own cold chain: one left out spoils, the rest of its batch in the cold does not.
            if (container.Doses < 1 || IsInColdStorage(uid) || !_batches.TryGetValue(container.BatchId, out var batch))
                continue;

            var before = PackQuality(batch, container).Purity;
            var purity = NCPharmaBatchMath.ResolveDegradedPurity(tuning, batch.Actual.Stability, before, seconds);
            if (purity >= before)
                continue;

            container.PurityLoss = MathF.Max(0f, batch.Actual.Purity - purity);
            if (_exposed.Add(batch.BatchId))
                batch.LoggedPurity = float.IsNaN(batch.LoggedPurity) ? before : batch.LoggedPurity;
            if (batch.LoggedPurity - purity >= AuditPurityStep)
            {
                batch.LoggedPurity = purity;
                AddAudit(batch, tuning, NCPharmaAuditKind.Degraded, "cold chain",
                    $"a pack left out of cold storage fell to purity {purity:P1} (stability {batch.Actual.Stability:P0})");
            }
        }
    }

    /// <summary>What a pack really holds: its batch, less what the pack itself lost to warmth.</summary>
    public NCPharmaQuality PackQuality(NCPharmaBatchRecord batch, NCPharmaContainerComponent pack) =>
        batch.Actual with { Purity = Math.Clamp(batch.Actual.Purity - MathF.Max(0f, pack.PurityLoss), 0f, 1f) };

    /// <summary>True when the entity sits, at any depth, inside a cold storage that is working.</summary>
    public bool IsInColdStorage(EntityUid entity)
    {
        var current = entity;
        for (var depth = 0; depth < MaximumContainerDepth; depth++)
        {
            if (!_containers.TryGetContainingContainer(current, out var container))
                return false;

            var owner = container.Owner;
            if (HasComp<NCPharmaColdStorageComponent>(owner))
                return _power.IsPowered(owner);

            current = owner;
        }

        return false;
    }
}
