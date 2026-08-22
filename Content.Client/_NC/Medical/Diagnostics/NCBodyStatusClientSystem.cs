// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Medical.Diagnostics;
using Robust.Client.Player;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Medical.Diagnostics;

/// <summary>
/// Owns the directed, owner-only body-status stream. Combat targeting intentionally does not depend on it.
/// </summary>
public sealed partial class NCBodyStatusClientSystem : EntitySystem
{
    private const byte MaximumSeverity = 8;

    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public event Action? StatusChanged;

    public ProtoId<NCAnatomyProfilePrototype>? ActiveProfile { get; private set; }
    public IReadOnlyList<NCMedicalZoneStatus> Statuses => _statuses;

    private readonly List<NCMedicalZoneStatus> _statuses = [];
    private EntityUid? _activeBody;
    private uint _nextRequestId;
    private uint? _latestRequestId;
    private uint _revision;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<NCMedicalStatusSnapshotResponse>(OnSnapshot);
        // Local-player events are both directed and broadcast. The broadcast form lets this presentation
        // system coexist with the targeting system, which owns the single component-directed subscription.
        SubscribeLocalEvent<LocalPlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<LocalPlayerDetachedEvent>(OnPlayerDetached);
        SubscribeLocalEvent<NCMedicalBodyComponent, ComponentRemove>(OnBodyRemoved);
    }

    public void RefreshLocalPlayer()
    {
        if (_players.LocalEntity is not { Valid: true } player ||
            !TryComp<NCMedicalBodyComponent>(player, out var body))
        {
            Clear();
            return;
        }

        Activate(player, body.Profile);
    }

    private void OnPlayerAttached(LocalPlayerAttachedEvent args)
    {
        if (_players.LocalEntity != args.Entity ||
            !TryComp<NCMedicalBodyComponent>(args.Entity, out var body))
        {
            Clear();
            return;
        }

        Activate(args.Entity, body.Profile);
    }

    private void OnPlayerDetached(LocalPlayerDetachedEvent args)
    {
        if (_activeBody == args.Entity)
            Clear();
    }

    private void OnBodyRemoved(Entity<NCMedicalBodyComponent> entity, ref ComponentRemove args)
    {
        if (_activeBody == entity.Owner)
            Clear();
    }

    private void Activate(EntityUid body, ProtoId<NCAnatomyProfilePrototype> profile)
    {
        if (!_prototypes.HasIndex(profile))
        {
            Clear();
            return;
        }

        // LocalPlayerAttachedEvent and gameplay-HUD creation can both activate the same body. Preserve the current
        // owner-only stream instead of issuing a duplicate snapshot query.
        if (_activeBody == body && ActiveProfile == profile)
            return;

        _activeBody = body;
        ActiveProfile = profile;
        _statuses.Clear();
        _revision = 0;
        RequestSnapshot();
        StatusChanged?.Invoke();
    }

    private void OnSnapshot(NCMedicalStatusSnapshotResponse message)
    {
        if (message.RequestId != 0)
        {
            if (_latestRequestId != message.RequestId)
                return;
            _latestRequestId = null;
        }

        if (!message.Accepted)
        {
            _statuses.Clear();
            _revision = 0;
            StatusChanged?.Invoke();
            return;
        }

        if (ActiveProfile is not { } profileId ||
            !_prototypes.TryIndex(profileId, out var profile) ||
            message.Revision < _revision)
        {
            return;
        }

        var byZone = new Dictionary<ProtoId<NCTargetZonePrototype>, NCMedicalZoneStatus>();
        foreach (var status in message.Zones)
        {
            if (status.Severity > MaximumSeverity || !profile.TargetZones.Contains(status.Zone))
                continue;

            if (!byZone.TryGetValue(status.Zone, out var current) || status.Severity >= current.Severity)
                byZone[status.Zone] = status;
        }

        _statuses.Clear();
        foreach (var zone in profile.TargetZones)
        {
            if (byZone.TryGetValue(zone, out var status))
                _statuses.Add(status);
        }
        _revision = message.Revision;
        StatusChanged?.Invoke();
    }

    private void RequestSnapshot()
    {
        unchecked
        {
            _nextRequestId++;
            if (_nextRequestId == 0)
                _nextRequestId++;
        }

        _latestRequestId = _nextRequestId;
        RaiseNetworkEvent(new NCMedicalStatusSnapshotRequest(_nextRequestId));
    }

    private void Clear()
    {
        if (_activeBody == null && ActiveProfile == null && _statuses.Count == 0 && _latestRequestId == null)
            return;

        _activeBody = null;
        ActiveProfile = null;
        _statuses.Clear();
        _latestRequestId = null;
        _revision = 0;
        StatusChanged?.Invoke();
    }
}
