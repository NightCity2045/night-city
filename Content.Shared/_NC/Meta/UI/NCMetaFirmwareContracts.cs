// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Meta.UI;

[Serializable, NetSerializable]
public sealed record NCMetaFirmwarePreview(Guid ReleaseId, long ArtifactResidentBytes, long ProcessMemoryBytes,
    long PackageBytes, List<string> RequiredScopes, List<NCMetaLinkSummary> Links, bool Eligible, bool Maintenance,
    string NoticeKey, Guid ProductId = default, Guid InstallationId = default, long InstallationRevision = 0,
    string PackageName = "", string Version = "");

[Serializable, NetSerializable]
public sealed record NCMetaFirmwareSelection(Guid ProductId = default) : NCMetaWorkspacePayload;

/// <summary>Consent over the exact server-admitted immutable release and its independent memory/storage costs.</summary>
[Serializable, NetSerializable]
public sealed record NCMetaFirmwareInstallRequest(List<NCMetaSoftwareBinding> Bindings, bool ScopeConsent,
    List<string> ApprovedScopes, bool ResourceConsent, long ApprovedArtifactResidentBytes,
    long ApprovedProcessMemoryBytes, long ApprovedPackageBytes, Guid ProductId = default,
    Guid ExpectedInstallationId = default, long ExpectedInstallationRevision = 0) : NCMetaWorkspacePayload;
