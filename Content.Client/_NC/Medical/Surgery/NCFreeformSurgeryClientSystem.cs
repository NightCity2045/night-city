// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.ActionResolution;
using Content.Shared._NC.Combat.Targeting;
using Content.Shared._NC.Medical.Surgery;
using Content.Shared._NC.Medical.Wounds;
using Robust.Client.UserInterface;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Medical.Surgery;

/// <summary>
/// Owns the private surgical window and correlates every mutation with the active server session/revision.
/// Automatic refresh keeps physiology, held tools and the field current while the player is operating.
/// </summary>
public sealed partial class NCFreeformSurgeryClientSystem : EntitySystem
{
    private const float RefreshInterval = 0.5f;
    private const float DepthSendInterval = 0.08f;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IUserInterfaceManager _ui = default!;

    private NCFreeformSurgeryWindow? _window;
    private NCFreeformSurgeryState? _state;
    private NCActionResolutionId _sessionId;
    private uint _sequence;
    private float _refreshTimer;
    private float _predictedDepth;
    private float _depthSendTimer;
    private bool _depthDirty;
    private bool _depthInFlight;
    private bool _interactionPending;
    private uint _pendingInteractionSequence;
    private bool _closing;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCFreeformSurgeryState>(OnState);
    }

    public override void Shutdown()
    {
        CloseWindow(sendClose: false);
        base.Shutdown();
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);
        if (_window is not { Disposed: false, IsOpen: true } || !_sessionId.IsValid)
            return;

        if (_depthDirty && _state != null)
        {
            _depthSendTimer -= frameTime;
            if (_depthSendTimer <= 0f)
            {
                _depthDirty = false;
                _depthInFlight = true;
                RaiseNetworkEvent(new NCFreeformSurgerySelectionRequest(
                    _sessionId,
                    _state.SelectedZone,
                    _state.Surface,
                    _predictedDepth));
            }
        }

        _refreshTimer -= frameTime;
        if (_refreshTimer > 0f)
            return;

        _refreshTimer = RefreshInterval;
        RaiseNetworkEvent(new NCFreeformSurgeryRefreshRequest(_sessionId));
    }

    private void OnState(NCFreeformSurgeryState state)
    {
        if (!state.SessionId.IsValid)
            return;

        // Fatal/session validation replies intentionally contain no anatomical payload. They may also be the
        // first reply received by a newly opened client, so never pass their empty ProtoId into PrototypeManager.
        if (!state.Accepted && string.IsNullOrEmpty(state.AnatomyProfile.Id))
        {
            _sessionId = state.SessionId;
            EnsureWindow();
            if (_interactionPending)
            {
                _interactionPending = false;
                _pendingInteractionSequence = 0;
                _window!.ResolvePendingInteraction();
            }
            _window!.ShowFailure(state.Failure);
            _refreshTimer = RefreshInterval;
            if (!_window.IsOpen)
            {
                _ui.ClickSound();
                _window.OpenCentered();
            }
            return;
        }

        // Defense in depth for a malformed accepted packet: retain the last readable field and request a later
        // refresh instead of allowing a null prototype key to terminate the client game thread.
        if (string.IsNullOrEmpty(state.AnatomyProfile.Id))
            return;

        var resolvePendingInteraction = false;
        var interactionDepthAcknowledged = false;
        if (_sessionId != state.SessionId)
        {
            resolvePendingInteraction = _interactionPending;
            _sessionId = state.SessionId;
            _interactionPending = false;
            _pendingInteractionSequence = 0;
            _predictedDepth = state.SelectedDepth;
            _depthDirty = false;
            _depthInFlight = false;
            _depthSendTimer = 0f;
        }
        else if (_interactionPending &&
                 (state.LastSequence >= _pendingInteractionSequence || !state.Accepted))
        {
            // Accepted refreshes with an older sequence are unrelated telemetry and must not erase prediction.
            // A matching sequence atomically replaces the predicted site state; a rejection rolls it back.
            _interactionPending = false;
            _pendingInteractionSequence = 0;
            resolvePendingInteraction = true;
            interactionDepthAcknowledged = true;
        }

        // A refresh can arrive while the thumb is moving. Only reconcile once the server has acknowledged the
        // latest predicted depth; otherwise the slider would visibly jump between old and local values.
        if (interactionDepthAcknowledged && !_depthDirty)
        {
            // NC: The server may stop a deep requested cut at an intact bone casing. A matching DoAfter reply is
            // the authoritative depth acknowledgement, so display the rib level instead of leaving a fake 88%.
            _predictedDepth = state.SelectedDepth;
            _depthInFlight = false;
        }
        else if (MathF.Abs(state.SelectedDepth - _predictedDepth) <= 0.001f)
        {
            _depthDirty = false;
            _depthInFlight = false;
        }
        else if (!_depthDirty && !_depthInFlight)
        {
            _predictedDepth = state.SelectedDepth;
        }

        // The acknowledged value comes from the authoritative session. This remains correct when an automatic
        // refresh reply overtakes a mutation reply and avoids a permanently desynchronized surgery cursor.
        _sequence = state.LastSequence;

        _state = state;
        EnsureWindow();
        if (resolvePendingInteraction)
            _window!.ResolvePendingInteraction();
        _window!.SetState(state, _predictedDepth);
        _refreshTimer = RefreshInterval;
        if (!_window.IsOpen)
        {
            _ui.ClickSound();
            _window.OpenCentered();
        }
    }

    private void EnsureWindow()
    {
        if (_window is { Disposed: false })
            return;

        _window = new NCFreeformSurgeryWindow(_prototypes);
        _window.DepthPredictionChanged += OnDepthPredictionChanged;
        _window.CapabilityChanged += OnCapabilityChanged;
        _window.InteractionRequested += OnInteractionRequested;
        _window.AttachRequested += OnAttachRequested;
        _window.SiteRequested += OnSiteRequested;
        _window.DetachRequested += OnDetachRequested;
        _window.OnClose += OnWindowClosed;
    }

    private void OnDepthPredictionChanged(float depth)
    {
        if (_state == null || !_sessionId.IsValid)
            return;

        var predicted = Math.Clamp(depth, 0f, 1f);
        if (MathF.Abs(predicted - _predictedDepth) <= 0.0001f)
            return;

        _predictedDepth = predicted;
        if (!_depthDirty)
            _depthSendTimer = DepthSendInterval;
        _depthDirty = true;
    }

    private void OnCapabilityChanged(ProtoId<NCSurgeryToolCapabilityPrototype> capability)
    {
        if (!_sessionId.IsValid)
            return;
        RaiseNetworkEvent(new NCFreeformSurgeryCapabilityRequest(_sessionId, capability));
    }

    private void OnInteractionRequested(
        ProtoId<NCTargetZonePrototype> zone,
        NCWoundSurface surface,
        Guid? woundId,
        int? structureHandle,
        float structureMinimumDepth,
        Vector2 position)
    {
        if (_state == null || !_sessionId.IsValid || _interactionPending)
            return;

        unchecked
        {
            _sequence++;
            if (_sequence == 0)
                _sequence++;
        }
        _interactionPending = true;
        _pendingInteractionSequence = _sequence;
        _depthDirty = false;
        _depthInFlight = true;
        RaiseNetworkEvent(new NCInteractiveSurgeryActionRequest(
            _sessionId,
            _sequence,
            zone == _state.SelectedZone ? _state.Revision : 0,
            zone,
            surface,
            structureHandle != null
                ? Math.Clamp(MathF.Max(_predictedDepth, structureMinimumDepth), 0f, 1f)
                : _predictedDepth,
            position,
            woundId,
            structureHandle));
    }

    private void OnAttachRequested(
        ProtoId<NCTargetZonePrototype> zone,
        NCWoundSurface surface,
        Vector2 position)
    {
        if (_state == null || !_sessionId.IsValid)
            return;
        RaiseNetworkEvent(new NCFreeformSurgeryAttachRequest(
            _sessionId,
            zone == _state.SelectedZone ? _state.Revision : 0,
            zone,
            surface,
            position));
    }

    private void OnDetachRequested(int handle)
    {
        if (_state == null || !_sessionId.IsValid)
            return;
        RaiseNetworkEvent(new NCFreeformSurgeryDetachRequest(_sessionId, _state.Revision, handle));
    }

    private void OnSiteRequested(
        ProtoId<NCTargetZonePrototype> zone,
        NCWoundSurface surface,
        Guid? incisionId)
    {
        if (_state == null || !_sessionId.IsValid)
            return;

        RaiseNetworkEvent(new NCFreeformSurgerySiteRequest(
            _sessionId,
            zone,
            surface,
            incisionId));
    }

    private void OnWindowClosed()
    {
        if (!_closing && _sessionId.IsValid)
            RaiseNetworkEvent(new NCFreeformSurgeryCloseRequest(_sessionId));
        ClearSession();
    }

    private void CloseWindow(bool sendClose)
    {
        if (sendClose && _sessionId.IsValid)
            RaiseNetworkEvent(new NCFreeformSurgeryCloseRequest(_sessionId));
        if (_window is { Disposed: false })
        {
            _closing = true;
            _window.Close();
            _closing = false;
        }
        _window = null;
        ClearSession();
    }

    private void ClearSession()
    {
        _state = null;
        _sessionId = default;
        _sequence = 0;
        _interactionPending = false;
        _pendingInteractionSequence = 0;
        _refreshTimer = 0f;
        _predictedDepth = 0f;
        _depthSendTimer = 0f;
        _depthDirty = false;
        _depthInFlight = false;
    }
}
