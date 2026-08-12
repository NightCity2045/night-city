// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Diagnostics.Detail;
using Robust.Client.UserInterface;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.Medical.Diagnostics;

/// <summary>
/// Correlates private diagnostic replies and opens a bounded presenter for the server-sanitized snapshot.
/// </summary>
public sealed partial class NCDiagnosticsClientSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IUserInterfaceManager _ui = default!;

    private NCDiagnosticWindow? _window;
    private uint _nextRequestId;
    private uint? _pendingRequestId;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCDiagnosticSnapshotResponse>(OnSnapshotResponse);
    }

    public override void Shutdown()
    {
        _window?.Close();
        _window = null;
        _pendingRequestId = null;
        base.Shutdown();
    }

    /// <summary>
    /// UI-ready explicit request. The server still derives the actor from the session and validates target/tool.
    /// </summary>
    public bool RequestSnapshot(
        EntityUid target,
        EntityUid? tool,
        NCDiagnosticTier tier)
    {
        if (!Exists(target) || tool is { } toolUid && !Exists(toolUid))
            return false;

        unchecked
        {
            _nextRequestId++;
            if (_nextRequestId == 0)
                _nextRequestId++;
        }

        _pendingRequestId = _nextRequestId;
        RaiseNetworkEvent(new NCDiagnosticSnapshotRequest(
            _nextRequestId,
            GetNetEntity(target),
            tool is { } observedTool ? GetNetEntity(observedTool) : null,
            tier));
        return true;
    }

    private void OnSnapshotResponse(NCDiagnosticSnapshotResponse response)
    {
        // Request id zero is the server-side scanner interaction path. Explicit replies must match the newest nonce.
        if (response.RequestId != 0)
        {
            if (_pendingRequestId != response.RequestId)
                return;
            _pendingRequestId = null;
        }

        EnsureWindow();
        if (!response.Accepted || response.Snapshot == null)
            _window!.ShowFailure(response.Failure);
        else
            _window!.SetSnapshot(response.Snapshot);

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

        _window = new NCDiagnosticWindow(_prototypes);
    }
}
