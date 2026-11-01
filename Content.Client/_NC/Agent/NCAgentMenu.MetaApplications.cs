// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Client._NC.Meta.UI;

namespace Content.Client._NC.Agent;

public sealed partial class NCAgentMenu
{
    private readonly NCMetaCitiNetBrowser _metaBrowser = new();
    public event Action<string>? MetaSiteRequested
    {
        add => _metaBrowser.SiteRequested += value;
        remove => _metaBrowser.SiteRequested -= value;
    }

    public void ShowMetaSiteNotice(string notice) => _metaBrowser.ShowNotice(notice);
    public bool TryShowMetaWebsite(NCMetaApplicationView view) => !Disposed && _metaBrowser.TryShowWebsite(view);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_metaBrowser.Disposed) _metaBrowser.Dispose();
        base.Dispose(disposing);
    }
}
