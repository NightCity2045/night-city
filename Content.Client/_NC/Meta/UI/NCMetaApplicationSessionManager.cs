// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Meta.UI;

namespace Content.Client._NC.Meta.UI;

/// <summary>Private application sessions share one budget; websites render inside the CitiNet browser.</summary>
public sealed class NCMetaApplicationSessionManager : IDisposable
{
    private readonly Dictionary<Guid, NCMetaApplicationView> _sessions = [];
    private readonly Dictionary<Guid, NCMetaApplicationWindow> _windows = [];
    private readonly Func<NCMetaApplicationView, bool>? _presentWebsite;
    private Guid _websiteSession;
    private bool _disposed;
    public event Action<BoundUserInterfaceMessage>? Requested;
    public event Action<string>? WebsiteNotice;
    public int SessionCount => _sessions.Count;

    public NCMetaApplicationSessionManager(Func<NCMetaApplicationView, bool>? presentWebsite = null)
    {
        _presentWebsite = presentWebsite;
    }

    public bool TryApplyState(NCMetaAppState state)
    {
        if (_disposed || state.SessionId == Guid.Empty)
            return false;
        if (state.Closed)
        {
            if (_websiteSession == state.SessionId) WebsiteNotice?.Invoke(state.Notice);
            Close(state.SessionId);
            return true;
        }
        if (!_sessions.TryGetValue(state.SessionId, out var view))
        {
            if (!state.FullSnapshot || _sessions.Count >= 4)
                return false;
            var sessionId = state.SessionId;
            if (state.Website)
            {
                if (_presentWebsite == null) return false;
                CloseWebsite();
                view = new NCMetaApplicationView();
                if (!_presentWebsite(view))
                {
                    view.Dispose();
                    return false;
                }
                _websiteSession = sessionId;
            }
            else
            {
                var window = new NCMetaApplicationWindow();
                view = window.ApplicationView;
                window.OnClose += () =>
                {
                    _windows.Remove(sessionId);
                    if (_sessions.Remove(sessionId))
                        Requested?.Invoke(new NCMetaAppCloseMessage(sessionId));
                    window.Dispose();
                };
                _windows.Add(sessionId, window);
                window.OpenCentered();
            }
            view.Requested += message => Requested?.Invoke(message);
            _sessions.Add(sessionId, view);
        }
        view.ApplyState(state);
        return true;
    }

    public void ApplyResult(NCMetaAppActionResultMessage result)
    {
        if (_sessions.TryGetValue(result.SessionId, out var view))
            view.ApplyResult(result);
    }

    public void Close(Guid sessionId)
    {
        if (!_sessions.Remove(sessionId, out var view))
            return;
        if (_websiteSession == sessionId) _websiteSession = Guid.Empty;
        if (_windows.Remove(sessionId, out var window))
        {
            window.Close();
            window.Dispose();
        }
        else
        {
            view.Parent?.RemoveChild(view);
            view.Dispose();
        }
    }

    public void CloseWebsite()
    {
        if (_websiteSession == Guid.Empty) return;
        Requested?.Invoke(new NCMetaAppCloseMessage(_websiteSession));
        Close(_websiteSession);
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
        Requested = null;
        WebsiteNotice = null;
    }
}
