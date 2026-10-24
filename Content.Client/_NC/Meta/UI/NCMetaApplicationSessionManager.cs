// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Meta.Mui;
using Content.Shared._NC.Meta.UI;

namespace Content.Client._NC.Meta.UI;

/// <summary>Reusable presentation host for private Agent/CitiNet routes. All routes share one four-window budget.</summary>
public sealed class NCMetaApplicationSessionManager : IDisposable
{
    private readonly Dictionary<Guid, NCMetaApplicationWindow> _sessions = [];
    private readonly NCMetaInvoiceConfirmationManager _invoices = new();
    private bool _disposed;
    public event Action<BoundUserInterfaceMessage>? Requested;
    public int SessionCount => _sessions.Count;

    public NCMetaApplicationSessionManager() => _invoices.Requested += message => Requested?.Invoke(message);
    public bool TryReceiveInvoice(BoundUserInterfaceMessage message) => !_disposed && _invoices.TryReceive(message);

    public bool TryApplyState(NCMetaAppState state)
    {
        if (_disposed || state.SessionId == Guid.Empty)
            return false;
        if (state.Closed)
        {
            Close(state.SessionId);
            return true;
        }
        if (!_sessions.TryGetValue(state.SessionId, out var window))
        {
            if (!state.FullSnapshot || _sessions.Count >= MuiLimits.ClientSessions)
                return false;
            window = new NCMetaApplicationWindow();
            var sessionId = state.SessionId;
            window.Requested += message => Requested?.Invoke(message);
            window.OnClose += () =>
            {
                _invoices.CloseSession(sessionId);
                if (_sessions.Remove(sessionId))
                    Requested?.Invoke(new NCMetaAppCloseMessage(sessionId));
                window.Dispose();
            };
            _sessions.Add(sessionId, window);
            window.OpenCentered();
        }
        window.ApplyState(state);
        return true;
    }

    public void ApplyResult(NCMetaAppActionResultMessage result)
    {
        if (_sessions.TryGetValue(result.SessionId, out var window))
            window.ApplyResult(result);
    }

    public void Close(Guid sessionId)
    {
        _invoices.CloseSession(sessionId);
        if (!_sessions.Remove(sessionId, out var window))
            return;
        window.Close();
        window.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var session in _sessions.Keys.ToArray())
        {
            Requested?.Invoke(new NCMetaAppCloseMessage(session));
            Close(session);
        }
        _invoices.Dispose();
        Requested = null;
    }
}
