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
            if (container.Doses > 0 && !IsInColdStorage(uid))
                _exposed.Add(container.BatchId);
        }

        foreach (var batchId in _exposed)
        {
            if (!_batches.TryGetValue(batchId, out var batch))
                continue;

            var purity = NCPharmaBatchMath.ResolveDegradedPurity(
                tuning, batch.Actual.Stability, batch.Actual.Purity, seconds);
            if (purity >= batch.Actual.Purity)
                continue;

            batch.Actual = batch.Actual with { Purity = purity };
            if (float.IsNaN(batch.LoggedPurity))
                batch.LoggedPurity = purity + AuditPurityStep;
            if (batch.LoggedPurity - purity >= AuditPurityStep)
            {
                batch.LoggedPurity = purity;
                AddAudit(batch, tuning, NCPharmaAuditKind.Degraded, "cold chain",
                    $"purity fell to {purity:P1} (stability {batch.Actual.Stability:P0})");
            }
        }
    }

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
