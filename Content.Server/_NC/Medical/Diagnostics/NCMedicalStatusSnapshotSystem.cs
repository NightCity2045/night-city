// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Medical.Wounds;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Diagnostics;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Medical.Wounds;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Diagnostics;

/// <summary>
/// Builds the deliberately lossy owner targeting-doll snapshot. Full wound records never enter PVS or a
/// broadcast event; both query responses and event-driven refreshes address exactly one player session.
/// </summary>
public sealed partial class NCMedicalStatusSnapshotSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCAnatomySystem _anatomy = default!;
    [Dependency] private NCWoundSystem _wounds = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<NCMedicalStatusSnapshotRequest>(OnSnapshotRequest);
        SubscribeLocalEvent<NCWoundContainerComponent, NCMedicalBodyChangedEvent>(OnMedicalBodyChanged);
    }

    /// <summary>
    /// Returns every zone from the body's anatomy profile in stable profile order, including healthy zeroes.
    /// </summary>
    public List<NCMedicalZoneStatus> BuildOwnerStatusSnapshot(EntityUid body)
    {
        var result = new List<NCMedicalZoneStatus>();
        if (!TryComp<NCMedicalBodyComponent>(body, out var medical) ||
            !_anatomy.TryGetProfile((body, medical), out var anatomyProfile))
        {
            return result;
        }

        var container = _wounds.EnsureInitialized(body);
        if (!_prototypes.TryIndex(container.Profile, out var woundSystemProfile))
            return result;

        var burden = new Dictionary<ProtoId<NCTargetZonePrototype>, float>();
        var severityFloor = new Dictionary<ProtoId<NCTargetZonePrototype>, byte>();
        foreach (var zone in anatomyProfile.TargetZones)
        {
            burden[zone] = container.OverflowTraumaByZone.GetValueOrDefault(zone);
            severityFloor[zone] = 0;
        }

        foreach (var wound in container.Wounds.Values)
        {
            if (!burden.ContainsKey(wound.TargetZone) ||
                !_prototypes.TryIndex(wound.Prototype, out var woundPrototype))
            {
                continue;
            }

            burden[wound.TargetZone] +=
                MathF.Max(0f, wound.IntegrityDamage.Float()) *
                MathF.Max(0f, woundPrototype.StatusDamageMultiplier);
            if (woundSystemProfile.SeverityStatusLevels.TryGetValue(wound.Severity, out var floor))
                severityFloor[wound.TargetZone] = Math.Max(severityFloor[wound.TargetZone], floor);
        }

        foreach (var zone in anatomyProfile.TargetZones)
        {
            var level = ResolveBurdenLevel(burden[zone], woundSystemProfile.StatusDamageThresholds);
            level = Math.Max(level, severityFloor[zone]);
            result.Add(new NCMedicalZoneStatus(zone, (byte) Math.Clamp((int) level, 0, 8)));
        }

        return result;
    }

    private void OnSnapshotRequest(NCMedicalStatusSnapshotRequest request, EntitySessionEventArgs args)
    {
        if (args.SenderSession.AttachedEntity is not { Valid: true } body ||
            !Exists(body) ||
            !TryComp<NCMedicalBodyComponent>(body, out var medical))
        {
            RaiseNetworkEvent(
                new NCMedicalStatusSnapshotResponse(request.RequestId, false, 0, []),
                args.SenderSession);
            return;
        }

        RaiseNetworkEvent(
            new NCMedicalStatusSnapshotResponse(
                request.RequestId,
                true,
                medical.MedicalRevision,
                BuildOwnerStatusSnapshot(body)),
            args.SenderSession);
    }

    private void OnMedicalBodyChanged(
        Entity<NCWoundContainerComponent> entity,
        ref NCMedicalBodyChangedEvent args)
    {
        if ((args.Domains & NCMedicalDirtyDomains.Diagnostics) == 0 ||
            !TryComp<ActorComponent>(entity.Owner, out var actor) ||
            !TryComp<NCMedicalBodyComponent>(entity.Owner, out var medical))
        {
            return;
        }

        // RequestId zero denotes a server-pushed refresh and cannot acknowledge a client query.
        RaiseNetworkEvent(
            new NCMedicalStatusSnapshotResponse(
                0,
                true,
                medical.MedicalRevision,
                BuildOwnerStatusSnapshot(entity.Owner)),
            actor.PlayerSession);
    }

    private static byte ResolveBurdenLevel(float burden, IReadOnlyList<float> thresholds)
    {
        if (thresholds.Count != 8 || !IsStrictlyAscending(thresholds))
            throw new InvalidOperationException("NC status-doll thresholds must contain exactly eight values.");

        byte level = 0;
        for (var i = 0; i < thresholds.Count; i++)
        {
            if (burden < thresholds[i])
                break;
            level = (byte) (i + 1);
        }
        return level;
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
}
