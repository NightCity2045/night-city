// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.Meta.Persistence;

public sealed record NCMetaProjectFile(Guid Id, string Path, long Revision, string Digest, int Bytes);
public sealed record NCMetaRelease(Guid Id, string Digest, long SourceRevision, string CompilerBuild,
    Content.Meta.Language.MetaExportCertificate? Exports = null, NCMetaReleaseResource[]? Resources = null,
    NCMetaReleaseResource[]? Sources = null, NCMetaDependencyRef[]? Dependencies = null,
    NCMetaCommercialOrigin? CommercialOrigin = null, NCMetaBuildAuthorization? BuildAuthorization = null);
public sealed record NCMetaBuildAuthorization(string Owner, string DeviceId, string ArtifactDigest, string DependencyFingerprint);
/// <summary>Blob identity and verified container identity are deliberately distinct.</summary>
public sealed record NCMetaDependencyRef(Guid ProductId, Guid ReleaseId, string BlobDigest,
    string ArtifactDigest, string PackageIdentity, string Version);
/// <summary>Immutable provenance follows a purchased image through private copies and physical media.</summary>
public sealed record NCMetaCommercialOrigin(Guid ProductId, int Major, Guid PublisherProjectId, Guid ReleaseId);
public sealed record NCMetaReleaseResource(string Path, string Digest, int Bytes, Guid FileId = default);
public sealed record NCMetaProject(Guid Id, string Owner, string Name, long Revision,
    NCMetaProjectFile[] Files, NCMetaRelease[] Releases);
public sealed record NCMetaBinding(string Slot, string DeviceId, string ContractHash, string[] Scopes);
public sealed record NCMetaInstallation(Guid Id, string HostDeviceId, string Owner, Guid ProjectId,
    NCMetaRelease Active, NCMetaRelease? Recovery, NCMetaBinding[] Bindings, long Revision, bool AutoStart,
    string PackageIdentity = "", long MemoryLimitBytes = 0, string SlotKind = "User",
    Guid StateVolumeId = default, int StateSchemaVersion = 1, Guid RecoveryStateVolumeId = default,
    NCMetaBinding[]? RecoveryBindings = null, int RecoveryStateSchemaVersion = 1);
public sealed record NCMetaUserFile(string Path, string Digest, int Bytes, long Revision);
public sealed record NCMetaVolume(Guid Id, string Owner, int CapacityBytes, NCMetaUserFile[] Files, long Revision);
public sealed record NCMetaFileWriteAuthorization(string DeviceId, ulong AuthorityEpoch,
    NCMetaInstallation? Installation = null, ulong HostAuthorityEpoch = 0);

public sealed record NCMetaMutationOutcome(bool Success, string MessageKey, long Revision = 0);
public sealed record NCMetaDeviceRecord(string DeviceId, string Owner, Guid[] Projects, Guid ActiveProject, long Revision,
    ulong AuthorityEpoch = 2);
