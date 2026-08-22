// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Diagnostics;
using Content.Server._NC.Medical.Wounds;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Bleeding;
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

    private readonly HashSet<EntityUid> _pendingBodyUpdates = [];
    private readonly List<EntityUid> _bodyUpdateBuffer = [];
    private readonly Dictionary<EntityUid, NCMedicalZoneStatus[]> _lastOwnerProjections = [];
    private long _snapshotRequests;
    private long _bodyChangeEvents;
    private long _coalescedBodyChanges;
    private long _snapshotsBuilt;
    private long _pushesSent;
    private long _suppressedPushes;
    private double _lastBuildMilliseconds;
    private double _maximumBuildMilliseconds;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<NCMedicalStatusSnapshotRequest>(OnSnapshotRequest);
        SubscribeLocalEvent<NCWoundContainerComponent, NCMedicalBodyChangedEvent>(OnMedicalBodyChanged);
        SubscribeLocalEvent<NCMedicalBodyComponent, ComponentRemove>(OnMedicalBodyRemoved);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_pendingBodyUpdates.Count == 0)
            return;

        // Clear the set before building snapshots. A medical mutation raised during a build is retained for the
        // following tick instead of modifying the collection currently being processed.
        _bodyUpdateBuffer.Clear();
        _bodyUpdateBuffer.AddRange(_pendingBodyUpdates);
        _pendingBodyUpdates.Clear();

        foreach (var body in _bodyUpdateBuffer)
        {
            if (!Exists(body) ||
                !TryComp<ActorComponent>(body, out var actor) ||
                !TryComp<NCMedicalBodyComponent>(body, out var medical))
            {
                continue;
            }

            var zones = BuildTrackedOwnerStatusSnapshot(body);
            if (ProjectionEquals(_lastOwnerProjections.GetValueOrDefault(body), zones))
            {
                _suppressedPushes++;
                continue;
            }

            RememberProjection(body, zones);
            RaiseNetworkEvent(
                new NCMedicalStatusSnapshotResponse(
                    0,
                    true,
                    medical.MedicalRevision,
                    zones),
                actor.PlayerSession);
            _pushesSent++;
        }
    }

    public NCMedicalStatusSnapshotCounters GetRuntimeCounters()
    {
        return new NCMedicalStatusSnapshotCounters(
            _pendingBodyUpdates.Count,
            _lastOwnerProjections.Count,
            _snapshotRequests,
            _bodyChangeEvents,
            _coalescedBodyChanges,
            _snapshotsBuilt,
            _pushesSent,
            _suppressedPushes,
            _lastBuildMilliseconds,
            _maximumBuildMilliseconds);
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
            var flags = ResolvePresentationFlags(body, zone);
            if ((flags & NCMedicalZoneStatusFlags.FunctionLost) != 0)
                level = 8;
            result.Add(new NCMedicalZoneStatus(zone, (byte) Math.Clamp((int) level, 0, 8), flags));
        }

        return result;
    }

    private NCMedicalZoneStatusFlags ResolvePresentationFlags(
        EntityUid body,
        ProtoId<NCTargetZonePrototype> zoneId)
    {
        var flags = NCMedicalZoneStatusFlags.None;
        if (!_prototypes.TryIndex(zoneId, out var zone) ||
            !_anatomy.TryGetOrgan(body, zone.MedicalRegionCategory, out var region))
        {
            return NCMedicalZoneStatusFlags.Missing;
        }

        if ((TryComp<NCOrganIntegrityComponent>(region, out var integrity) &&
             integrity.FunctionalEfficiency <= 0.1f) ||
            (TryComp<NCNerveComponent>(region, out var nerve) && nerve.MotorFunction <= 0.1f))
        {
            flags |= NCMedicalZoneStatusFlags.FunctionLost;
        }

        if (TryComp<NCBleedingComponent>(body, out var bleeding))
        {
            foreach (var source in bleeding.Sources)
            {
                if (source.TargetZone == zoneId &&
                    source.Channel != NCBleedingChannel.Internal &&
                    source.CurrentRate > 0f)
                {
                    flags |= NCMedicalZoneStatusFlags.ExternalBleeding;
                    break;
                }
            }
        }

        return flags;
    }

    private void OnSnapshotRequest(NCMedicalStatusSnapshotRequest request, EntitySessionEventArgs args)
    {
        _snapshotRequests++;
        if (args.SenderSession.AttachedEntity is not { Valid: true } body ||
            !Exists(body) ||
            !TryComp<NCMedicalBodyComponent>(body, out var medical))
        {
            RaiseNetworkEvent(
                new NCMedicalStatusSnapshotResponse(request.RequestId, false, 0, []),
                args.SenderSession);
            return;
        }

        var zones = BuildTrackedOwnerStatusSnapshot(body);
        RememberProjection(body, zones);
        RaiseNetworkEvent(
            new NCMedicalStatusSnapshotResponse(
                request.RequestId,
                true,
                medical.MedicalRevision,
                zones),
            args.SenderSession);
    }

    private void OnMedicalBodyChanged(
        Entity<NCWoundContainerComponent> entity,
        ref NCMedicalBodyChangedEvent args)
    {
        if ((args.Domains & NCMedicalDirtyDomains.Diagnostics) == 0 ||
            !HasComp<ActorComponent>(entity.Owner) ||
            !HasComp<NCMedicalBodyComponent>(entity.Owner))
        {
            return;
        }

        _bodyChangeEvents++;
        if (!_pendingBodyUpdates.Add(entity.Owner))
            _coalescedBodyChanges++;
    }

    private void OnMedicalBodyRemoved(Entity<NCMedicalBodyComponent> entity, ref ComponentRemove args)
    {
        _pendingBodyUpdates.Remove(entity.Owner);
        _lastOwnerProjections.Remove(entity.Owner);
    }

    private void RememberProjection(EntityUid body, IReadOnlyList<NCMedicalZoneStatus> zones)
    {
        var copy = new NCMedicalZoneStatus[zones.Count];
        for (var index = 0; index < zones.Count; index++)
            copy[index] = zones[index];
        _lastOwnerProjections[body] = copy;
    }

    private static bool ProjectionEquals(
        IReadOnlyList<NCMedicalZoneStatus>? left,
        IReadOnlyList<NCMedicalZoneStatus> right)
    {
        if (left == null || left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
                return false;
        }

        return true;
    }

    private List<NCMedicalZoneStatus> BuildTrackedOwnerStatusSnapshot(EntityUid body)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return BuildOwnerStatusSnapshot(body);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _snapshotsBuilt++;
            _lastBuildMilliseconds = elapsed;
            _maximumBuildMilliseconds = Math.Max(_maximumBuildMilliseconds, elapsed);
        }
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

public readonly record struct NCMedicalStatusSnapshotCounters(
    int PendingBodies,
    int CachedProjections,
    long SnapshotRequests,
    long BodyChangeEvents,
    long CoalescedBodyChanges,
    long SnapshotsBuilt,
    long PushesSent,
    long SuppressedPushes,
    double LastBuildMilliseconds,
    double MaximumBuildMilliseconds);
