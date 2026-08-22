// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Anatomy;
using Robust.Client.Player;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Combat.Targeting;

/// <summary>
/// Owns the local predicted targeting intent and reconciles it with directed server acknowledgements.
/// The exact selected region is never stored in a networked component visible through PVS.
/// </summary>
public sealed partial class NCTargetingClientSystem : EntitySystem
{
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public event Action? TargetingChanged;

    public ProtoId<NCAnatomyProfilePrototype>? ActiveProfile { get; private set; }
    public NCTargetingState State { get; private set; } = AutomaticState;
    public bool HasPendingRequest => _latestRequestId != null;

    private static readonly NCTargetingState AutomaticState = new(NCTargetingMode.Automatic, null);

    private EntityUid? _activeBody;
    private uint _nextRequestId;
    private uint? _latestRequestId;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<NCTargetingSelectionAck>(OnSelectionAck);
        SubscribeLocalEvent<NCMedicalBodyComponent, LocalPlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<NCMedicalBodyComponent, LocalPlayerDetachedEvent>(OnPlayerDetached);
        SubscribeLocalEvent<NCMedicalBodyComponent, ComponentShutdown>(OnAnatomyShutdown);
    }

    /// <summary>
    /// Re-evaluates the controlled entity when the gameplay HUD is recreated after the attach event already fired.
    /// </summary>
    public void RefreshLocalPlayer()
    {
        if (_players.LocalEntity is not { Valid: true } player ||
            !TryComp<NCMedicalBodyComponent>(player, out var anatomy))
        {
            ClearLocalState();
            return;
        }

        ActivateProfile(player, anatomy.Profile, queryServer: true);
    }

    /// <summary>
    /// Applies responsive local feedback, then asks the server to validate the private selection.
    /// </summary>
    public bool RequestSelection(NCTargetingState requested)
    {
        if (!IsValidForActiveProfile(requested))
            return false;

        State = Normalize(requested);
        TargetingChanged?.Invoke();

        var requestId = BeginRequest();
        RaiseNetworkEvent(new NCTargetingSelectionRequest(requestId, State));
        return true;
    }

    private void OnPlayerAttached(
        Entity<NCMedicalBodyComponent> entity,
        ref LocalPlayerAttachedEvent args)
    {
        if (_players.LocalEntity != entity.Owner)
            return;

        ActivateProfile(entity.Owner, entity.Comp.Profile, queryServer: true);
    }

    private void OnPlayerDetached(
        Entity<NCMedicalBodyComponent> entity,
        ref LocalPlayerDetachedEvent args)
    {
        if (_activeBody == entity.Owner)
            ClearLocalState();
    }

    private void OnAnatomyShutdown(
        Entity<NCMedicalBodyComponent> entity,
        ref ComponentShutdown args)
    {
        if (_activeBody == entity.Owner)
            ClearLocalState();
    }

    private void OnSelectionAck(NCTargetingSelectionAck message)
    {
        // Older query/selection responses must never roll back a newer optimistic click.
        if (_latestRequestId != message.RequestId)
            return;

        _latestRequestId = null;
        State = IsValidForActiveProfile(message.State)
            ? Normalize(message.State)
            : AutomaticState;
        TargetingChanged?.Invoke();
    }

    private void ActivateProfile(
        EntityUid body,
        ProtoId<NCAnatomyProfilePrototype> profile,
        bool queryServer)
    {
        if (!_prototypes.HasIndex(profile))
        {
            ClearLocalState();
            return;
        }

        // Player attach and gameplay-HUD creation can occur in either order. The first activation owns the
        // authoritative query; rebuilding the same HUD must not send an identical request a second time.
        if (_activeBody == body && ActiveProfile == profile)
            return;

        _activeBody = body;
        ActiveProfile = profile;
        State = AutomaticState;
        _latestRequestId = null;
        TargetingChanged?.Invoke();

        if (!queryServer)
            return;

        var requestId = BeginRequest();
        RaiseNetworkEvent(new NCTargetingSelectionQuery(requestId));
    }

    private void ClearLocalState()
    {
        if (_activeBody == null && ActiveProfile == null && State == AutomaticState && _latestRequestId == null)
            return;

        _activeBody = null;
        ActiveProfile = null;
        State = AutomaticState;
        _latestRequestId = null;
        TargetingChanged?.Invoke();
    }

    private bool IsValidForActiveProfile(NCTargetingState state)
    {
        if (ActiveProfile is not { } profileId ||
            !_prototypes.TryIndex(profileId, out var profile))
        {
            return false;
        }

        return state.Mode switch
        {
            NCTargetingMode.Automatic => true,
            NCTargetingMode.SelectedZone => state.SelectedZone is { } zone &&
                                            profile.TargetZones.Contains(zone) &&
                                            _prototypes.HasIndex(zone),
            _ => false,
        };
    }

    private uint BeginRequest()
    {
        unchecked
        {
            _nextRequestId++;
            if (_nextRequestId == 0)
                _nextRequestId++;
        }

        _latestRequestId = _nextRequestId;
        return _nextRequestId;
    }

    private static NCTargetingState Normalize(NCTargetingState state)
    {
        return state.Mode == NCTargetingMode.Automatic
            ? AutomaticState
            : state;
    }
}
