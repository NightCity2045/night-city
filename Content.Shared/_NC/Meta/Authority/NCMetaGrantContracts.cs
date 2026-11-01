// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Meta.Authority;

/// <summary>Requested approval only; the native server resolves all identities, interfaces and epochs again.</summary>
[Serializable, NetSerializable]
public sealed record NCMetaGrantConsent(string Slot, string TargetDeviceId, List<string> Members, List<string> Scopes);

[Serializable, NetSerializable]
public sealed record NCMetaGrantSummary(Guid GrantId, Guid InstallationId, string HostDeviceId, string Principal,
    string Slot, string TargetDeviceId, string Capability, List<string> Members, List<string> Scopes,
    long Revision, long? ExpiresAtTicks, bool Unattended, bool Revoked);

[Serializable, NetSerializable]
public sealed record NCMetaGrantApprovalRequest(long ExpectedRevision, bool ScopeConsent, bool AllowUnattended,
    long? ExpiresAtTicks, List<NCMetaGrantConsent> Bindings) : Content.Shared._NC.Meta.UI.NCMetaWorkspacePayload;

[Serializable, NetSerializable]
public sealed record NCMetaGrantMember(string Name, string Scope);

[Serializable, NetSerializable]
public sealed record NCMetaGrantLink(string Slot, string TargetDeviceId, string Capability, string ContractHash,
    List<NCMetaGrantMember> Members);

[Serializable, NetSerializable]
public sealed record NCMetaGrantPolicySummary(Guid InstallationId, string HostDeviceId, string Principal,
    string ArtifactDigest, long Revision, bool AllowUnattended, bool Revoked,
    List<NCMetaGrantSummary> Grants, List<NCMetaGrantLink> Links, long ActiveWorldTicks = 0, long? ExpiresAtTicks = null);
