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
    private bool _strokePending;
    private uint _pendingStrokeSequence;
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

        // Fatal/session validation replies intentionally contain no anatomical payload. Preserve the last
        // readable field instead of replacing it with an empty default profile while refresh may recover.
        if (!state.Accepted && _state != null && string.IsNullOrEmpty(state.AnatomyProfile.Id))
        {
            EnsureWindow();
            if (_strokePending)
            {
                _strokePending = false;
                _pendingStrokeSequence = 0;
                _window!.ResolvePendingStroke();
            }
            _window!.ShowFailure(state.Failure);
            _refreshTimer = RefreshInterval;
            return;
        }

        var resolvePendingStroke = false;
        if (_sessionId != state.SessionId)
        {
            resolvePendingStroke = _strokePending;
            _sessionId = state.SessionId;
            _strokePending = false;
            _pendingStrokeSequence = 0;
            _predictedDepth = state.SelectedDepth;
            _depthDirty = false;
            _depthInFlight = false;
            _depthSendTimer = 0f;
        }
        else if (_strokePending &&
                 (state.LastSequence >= _pendingStrokeSequence || !state.Accepted))
        {
            // Accepted refreshes with an older sequence are unrelated telemetry and must not erase prediction.
            // A matching sequence atomically replaces it with authoritative geometry; a rejection rolls it back.
            _strokePending = false;
            _pendingStrokeSequence = 0;
            resolvePendingStroke = true;
        }

        // A refresh can arrive while the thumb is moving. Only reconcile once the server has acknowledged the
        // latest predicted depth; otherwise the slider would visibly jump between old and local values.
        if (MathF.Abs(state.SelectedDepth - _predictedDepth) <= 0.001f)
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
        if (resolvePendingStroke)
            _window!.ResolvePendingStroke();
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
        _window.SelectionChanged += OnSelectionChanged;
        _window.DepthPredictionChanged += OnDepthPredictionChanged;
        _window.CapabilityChanged += OnCapabilityChanged;
        _window.StrokeSubmitted += OnStrokeSubmitted;
        _window.AttachRequested += OnAttachRequested;
        _window.DetachRequested += OnDetachRequested;
        _window.OnClose += OnWindowClosed;
    }

    private void OnSelectionChanged(
        NCWoundSurface surface,
        float depth)
    {
        if (_state == null || !_sessionId.IsValid)
            return;
        _predictedDepth = Math.Clamp(depth, 0f, 1f);
        _depthDirty = false;
        _depthInFlight = true;
        RaiseNetworkEvent(new NCFreeformSurgerySelectionRequest(
            _sessionId,
            _state.SelectedZone,
            surface,
            depth));
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

    private void OnStrokeSubmitted(
        ProtoId<NCTargetZonePrototype> zone,
        Vector2[] points)
    {
        if (_state == null || !_sessionId.IsValid || _strokePending)
            return;

        unchecked
        {
            _sequence++;
            if (_sequence == 0)
                _sequence++;
        }
        _strokePending = true;
        _pendingStrokeSequence = _sequence;
        _depthDirty = false;
        _depthInFlight = true;
        RaiseNetworkEvent(new NCFreeformSurgeryStrokeRequest(
            _sessionId,
            _sequence,
            zone == _state.SelectedZone ? _state.Revision : 0,
            zone,
            _state.Surface,
            _predictedDepth,
            points));
    }

    private void OnAttachRequested(
        ProtoId<NCTargetZonePrototype> zone,
        Vector2 position)
    {
        if (_state == null || !_sessionId.IsValid)
            return;
        RaiseNetworkEvent(new NCFreeformSurgeryAttachRequest(
            _sessionId,
            zone == _state.SelectedZone ? _state.Revision : 0,
            zone,
            position));
    }

    private void OnDetachRequested(int handle)
    {
        if (_state == null || !_sessionId.IsValid)
            return;
        RaiseNetworkEvent(new NCFreeformSurgeryDetachRequest(_sessionId, _state.Revision, handle));
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
        _strokePending = false;
        _pendingStrokeSequence = 0;
        _refreshTimer = 0f;
        _predictedDepth = 0f;
        _depthSendTimer = 0f;
        _depthDirty = false;
        _depthInFlight = false;
    }
}
