// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using System.Text;
using Content.Shared._NC.Meta.Authority;
using Content.Shared._NC.Meta.UI;
using Robust.Client.UserInterface.Controls;

namespace Content.Client._NC.Meta.UI;

public sealed partial class NCMetaWorkspaceWindow
{
    private NCMetaGrantPolicySummary? _serviceGrants;
    private Guid _grantRequest;
    private Guid _grantInstallation;
    private Guid _grantRelease;
    private string _grantDevice = "";
    private string _grantSlot = "";
    private Guid _selectedGrant;
    private float _grantRequestSeconds;
    private readonly Dictionary<string, HashSet<string>> _grantMembers = new(StringComparer.Ordinal);
    private static readonly int[] GrantMinutes = [15, 60, 480, 0];

    private void InitializeServiceGrants()
    {
        TabContainer.SetTabTitle(ServiceGrantsTab, Loc.GetString("nc-meta-grants-tab"));
        GrantsDuration.AddItem(Loc.GetString("nc-meta-grants-minutes", ("minutes", 15)), 0);
        GrantsDuration.AddItem(Loc.GetString("nc-meta-grants-minutes", ("minutes", 60)), 1);
        GrantsDuration.AddItem(Loc.GetString("nc-meta-grants-minutes", ("minutes", 480)), 2);
        GrantsDuration.AddItem(Loc.GetString("nc-meta-grants-no-expiry"), 3);
        GrantsDuration.SelectId(0);
        GrantsDuration.OnItemSelected += args => GrantsDuration.SelectId(args.Id);
        GrantsScopeConsent.OnToggled += _ => UpdateServiceGrantControls();
        GrantsUnattended.OnToggled += _ => UpdateServiceGrantControls();
        GrantsRefresh.OnPressed += _ => RefreshServiceGrants();
        MainContentTabs.OnTabChanged += _ =>
        {
            if (ServiceGrantsTab.VisibleInTree && _serviceGrants == null && _grantRequest == Guid.Empty)
                RefreshServiceGrants();
        };
        GrantsLinks.OnItemSelected += args =>
        {
            if (_applying || _serviceGrants == null || args.ItemIndex < 0 || args.ItemIndex >= Math.Min(64, _serviceGrants.Links.Count)) return;
            _grantSlot = _serviceGrants.Links[args.ItemIndex].Slot;
            ShowServiceGrantMembers();
        };
        GrantsCurrent.OnItemSelected += args =>
        {
            if (_applying || _serviceGrants == null || args.ItemIndex < 0 || args.ItemIndex >= Math.Min(64, _serviceGrants.Grants.Count)) return;
            _selectedGrant = _serviceGrants.Grants[args.ItemIndex].GrantId;
            ShowSelectedServiceGrant();
            UpdateServiceGrantControls();
        };
        GrantsIssue.OnPressed += _ => IssueServiceGrants();
        GrantsRevoke.OnPressed += _ =>
        {
            if (GrantsRevoke.Disabled || !ServiceGrantTargetIsCurrent() || _serviceGrants is not { } policy) return;
            SendServiceGrantRequest(NCMetaWorkspaceOperation.RevokeServiceGrant, fileId: _selectedGrant, revision: policy.Revision);
        };
        GrantsRevokeAll.OnPressed += _ =>
        {
            if (!GrantsRevokeAll.Disabled && ServiceGrantTargetIsCurrent() && _serviceGrants is { } policy)
                SendServiceGrantRequest(NCMetaWorkspaceOperation.RevokeServiceGrant, revision: policy.Revision);
        };
        UpdateServiceGrantControls();
    }

    private bool ServiceGrantTargetIsCurrent() => _grantDevice == _selectedDeviceId &&
        _grantInstallation == _selectedInstallationId && _grantInstallation != Guid.Empty &&
        SelectedInstallation?.ReleaseId == _grantRelease;

    private void InvalidateServiceGrants()
    {
        _serviceGrants = null;
        _grantRequest = Guid.Empty;
        _grantRequestSeconds = 0;
        _grantInstallation = Guid.Empty;
        _grantRelease = Guid.Empty;
        _grantDevice = _grantSlot = "";
        _selectedGrant = Guid.Empty;
        _grantMembers.Clear();
        GrantsScopeConsent.Pressed = GrantsUnattended.Pressed = false;
        GrantsLinks.Clear();
        GrantsCurrent.Clear();
        GrantsDetails.Clear();
        NCMetaUiLifecycle.RemoveChildren(GrantsMembers);
    }

    private void RefreshServiceGrants()
    {
        if (SelectedInstallation == null || _selectedDeviceId.Length == 0 || _grantRequest != Guid.Empty) return;
        InvalidateServiceGrants();
        _grantInstallation = _selectedInstallationId;
        _grantDevice = _selectedDeviceId;
        _grantRelease = SelectedInstallation.ReleaseId;
        SendServiceGrantRequest(NCMetaWorkspaceOperation.ReadServiceGrants);
    }

    private void SendServiceGrantRequest(NCMetaWorkspaceOperation operation, string source = "", Guid fileId = default, long revision = 0,
        NCMetaWorkspacePayload? payload = null)
    {
        if (!ServiceGrantTargetIsCurrent() || _grantRequest != Guid.Empty) return;
        _grantRequest = Guid.NewGuid();
        _grantRequestSeconds = 0;
        Requested?.Invoke(new(operation, _grantRequest, fileId, revision, source, _grantDevice,
            _grantRelease, projectId: _state.ProjectId, installationId: _grantInstallation, payload: payload));
        UpdateServiceGrantControls();
    }

    private void ApplyServiceGrantState(NCMetaGrantPolicySummary? policy)
    {
        if (!ServiceGrantTargetIsCurrent()) InvalidateServiceGrants();
        var accepted = _grantRequest != Guid.Empty && _state.AcknowledgedRequestId == _grantRequest;
        if (accepted)
        {
            _grantRequest = Guid.Empty;
            // A failed read must not leave an old approval attached to a new installation.
            if (policy == null) InvalidateServiceGrants();
        }
        if (accepted && policy != null && ServiceGrantTargetIsCurrent() && policy.InstallationId == _grantInstallation &&
            policy.HostDeviceId == _grantDevice && policy.Links.Count <= 64 && policy.Grants.Count <= 64 &&
            policy.Links.All(link => link.Members.Count <= 128))
        {
            var changed = _serviceGrants == null || _serviceGrants.Revision != policy.Revision ||
                _serviceGrants.ArtifactDigest != policy.ArtifactDigest;
            _serviceGrants = policy;
            if (changed)
            {
                _grantMembers.Clear();
                _grantSlot = "";
                _selectedGrant = Guid.Empty;
                GrantsScopeConsent.Pressed = GrantsUnattended.Pressed = false;
                GrantsDuration.SelectId(0);
                GrantsLinks.Clear();
                foreach (var link in policy.Links)
                    GrantsLinks.AddItem(Loc.GetString("nc-meta-grants-link", ("slot", link.Slot),
                        ("interface", link.Capability), ("device", GrantDeviceName(link.TargetDeviceId))));
                GrantsCurrent.Clear();
                foreach (var grant in policy.Grants)
                    GrantsCurrent.AddItem(Loc.GetString("nc-meta-grants-current-link", ("slot", grant.Slot),
                        ("device", GrantDeviceName(grant.TargetDeviceId)), ("count", grant.Members.Count)));
                ShowServiceGrantMembers();
                ShowSelectedServiceGrant();
            }
        }
        UpdateServiceGrantControls();
    }

    private string GrantDeviceName(string deviceId) => _state.Devices.Take(64).FirstOrDefault(device => device.DeviceId == deviceId)?.Name
        ?? Loc.GetString("nc-meta-grants-device-unavailable");

    private void ShowServiceGrantMembers()
    {
        NCMetaUiLifecycle.RemoveChildren(GrantsMembers);
        if (_serviceGrants?.Links.FirstOrDefault(link => link.Slot == _grantSlot) is not { } link) return;
        if (!_grantMembers.TryGetValue(link.Slot, out var selected)) _grantMembers.Add(link.Slot, selected = new(StringComparer.Ordinal));
        var policy = _serviceGrants;
        foreach (var member in link.Members.Take(128))
        {
            var check = new CheckBox { Text = Loc.GetString("nc-meta-grants-member", ("member", member.Name), ("scope", member.Scope)),
                Pressed = selected.Contains(member.Name) };
            check.OnToggled += args =>
            {
                if (_serviceGrants is not { } current || current.Revision != policy.Revision || current.ArtifactDigest != policy.ArtifactDigest ||
                    _grantRequest != Guid.Empty) return;
                if (args.Pressed) selected.Add(member.Name); else selected.Remove(member.Name);
                GrantsScopeConsent.Pressed = false;
                UpdateServiceGrantControls();
            };
            GrantsMembers.AddChild(check);
        }
    }

    private void ShowSelectedServiceGrant()
    {
        GrantsDetails.Clear();
        if (_serviceGrants is not { } policy || policy.Grants.FirstOrDefault(grant => grant.GrantId == _selectedGrant) is not { } grant) return;
        foreach (var member in grant.Members.Take(128)) GrantsDetails.AddItem(member, selectable: false);
        foreach (var scope in grant.Scopes.Take(64)) GrantsDetails.AddItem(Loc.GetString("nc-meta-market-scope", ("scope", scope)), selectable: false);
        var expiry = grant.ExpiresAtTicks is { } until
            ? Loc.GetString("nc-meta-grants-remaining", ("minutes", Math.Max(0, (until - policy.ActiveWorldTicks) / TimeSpan.TicksPerMinute)))
            : Loc.GetString("nc-meta-grants-no-expiry");
        GrantsDetails.AddItem(expiry, selectable: false);
    }

    private void IssueServiceGrants()
    {
        if (GrantsIssue.Disabled || !ServiceGrantTargetIsCurrent() || _serviceGrants is not { } policy ||
            !GrantsScopeConsent.Pressed || GrantsDuration.SelectedId is < 0 or > 3) return;
        var duration = GrantMinutes[GrantsDuration.SelectedId];
        if (duration != 0 && policy.ActiveWorldTicks > long.MaxValue - duration * TimeSpan.TicksPerMinute) return;
        long? expires = duration == 0 ? null : policy.ActiveWorldTicks + duration * TimeSpan.TicksPerMinute;
        var bindings = policy.Links.Select(link =>
        {
            var members = link.Members.Where(member => _grantMembers.TryGetValue(link.Slot, out var selected) && selected.Contains(member.Name)).ToList();
            return new NCMetaGrantConsent(link.Slot, link.TargetDeviceId, members.Select(member => member.Name).Distinct(StringComparer.Ordinal).ToList(),
                members.Select(member => member.Scope).Distinct(StringComparer.Ordinal).ToList());
        }).ToList();
        SendServiceGrantRequest(NCMetaWorkspaceOperation.IssueServiceGrants,
            payload: new NCMetaGrantApprovalRequest(policy.Revision, true, GrantsUnattended.Pressed, expires, bindings));
    }

    private void UpdateServiceGrantControls()
    {
        if (_grantInstallation != Guid.Empty && !ServiceGrantTargetIsCurrent()) InvalidateServiceGrants();
        var available = SelectedInstallation != null && _state.Devices.Take(64).Any(device => device.DeviceId == _selectedDeviceId);
        var pending = _grantRequest != Guid.Empty;
        GrantsRefresh.Disabled = !available || pending;
        GrantsInstallation.Text = SelectedInstallation is { } installation
            ? Loc.GetString("nc-meta-grants-installation", ("name", installation.Name), ("device", GrantDeviceName(_selectedDeviceId)))
            : Loc.GetString("nc-meta-grants-select-installation");
        GrantsPolicy.Text = _serviceGrants is { } policy ? Loc.GetString(policy.Revoked ? "nc-meta-grants-policy-revoked" : "nc-meta-grants-policy",
            ("revision", policy.Revision), ("mode", Loc.GetString(policy.AllowUnattended ? "nc-meta-grants-mode-unattended" : "nc-meta-grants-mode-attended")))
            : Loc.GetString(pending ? "nc-meta-grants-loading" : "nc-meta-grants-refresh-required");
        GrantsIssue.Disabled = !ServiceGrantTargetIsCurrent() || _serviceGrants == null || pending || !GrantsScopeConsent.Pressed;
        GrantsRevoke.Disabled = pending || !ServiceGrantTargetIsCurrent() || _serviceGrants?.Grants.Any(grant =>
            grant.GrantId == _selectedGrant && !grant.Revoked) != true;
        GrantsRevokeAll.Disabled = pending || !ServiceGrantTargetIsCurrent() || _serviceGrants is not { Revision: > 0, Revoked: false };
        if (_serviceGrants is { ExpiresAtTicks: { } expiry } expiring)
            GrantsPolicy.ToolTip = Loc.GetString("nc-meta-grants-remaining", ("minutes", Math.Max(0, (expiry - expiring.ActiveWorldTicks) / TimeSpan.TicksPerMinute)));
        else GrantsPolicy.ToolTip = _serviceGrants == null ? "" : Loc.GetString("nc-meta-grants-no-expiry");
        GrantsScopeConsent.Disabled = GrantsUnattended.Disabled = GrantsDuration.Disabled = pending || _serviceGrants == null;
        foreach (var child in GrantsMembers.Children.OfType<CheckBox>()) child.Disabled = pending;
    }

    private void RejectServiceGrantRequest(Guid acknowledged)
    {
        if (_grantRequest == Guid.Empty || acknowledged != _grantRequest) return;
        InvalidateServiceGrants();
        UpdateServiceGrantControls();
    }

    private void TickServiceGrantRequest(float seconds)
    {
        if (_grantRequest == Guid.Empty) return;
        _grantRequestSeconds += seconds;
        if (_grantRequestSeconds < 15) return;
        InvalidateServiceGrants();
        UpdateServiceGrantControls();
        Status.Text = Loc.GetString("nc-meta-grants-unconfirmed");
    }
}
