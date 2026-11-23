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
    /// <summary>Raised when the last session is gone, so the owner of the connection can let go of it.</summary>
    public event Action? Emptied;
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
            NCMetaApplicationView? created = null;
            if (state.Website)
            {
                CloseWebsite();
                created = new NCMetaApplicationView();
                // A browser window shows the page inside itself; without one a page gets a window of its own.
                var hosted = _presentWebsite != null ? _presentWebsite(created) : TryHostInWindows(created);
                if (!hosted)
                {
                    created.Dispose();
                    created = null;
                    if (_presentWebsite != null) return false;
                }
                else _websiteSession = sessionId;
            }
            if (created == null)
            {
                var window = new NCMetaApplicationWindow();
                created = window.ApplicationView;
                window.OnClose += () =>
                {
                    _windows.Remove(sessionId);
                    if (_sessions.Remove(sessionId))
                        Requested?.Invoke(new NCMetaAppCloseMessage(sessionId));
                    // The page shown inside a browser window goes with it.
                    CloseWebsite();
                    window.Dispose();
                    if (_sessions.Count == 0) Emptied?.Invoke();
                };
                _windows.Add(sessionId, window);
                window.OpenCentered();
            }
            view = created;
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

    private bool TryHostInWindows(NCMetaApplicationView page)
    {
        foreach (var window in _windows.Values)
        {
            if (window.SiteHost is not { Disposed: false, ChildCount: 0 } host) continue;
            host.AddChild(page);
            return true;
        }
        return false;
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
