// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Meta.UI;

[Serializable, NetSerializable]
public enum NCMetaUiKey : byte { Workspace, Application }

[Serializable, NetSerializable]
public enum NCMetaSaveStatus : byte { None, ChunkAccepted, Committed, Rejected }

[Serializable, NetSerializable]
public enum NCMetaWorkspaceOperation : byte
{
    Open, Read, Save, Compile, Install, Start, Stop, Logs, Test,
    DebugAttach, DebugDetach, DebugPause, DebugContinue, DebugStepInto, DebugStepOver, DebugStepOut,
    DebugSetBreakpoint, DebugEvaluate, Rollback,
    CreateProject, SelectProject, RenameProject, DeleteProject, CreateFile, RenameFile, DeleteFile, SaveChunk, OpenApplication,
    PublishWebsite, WithdrawWebsite, ExportMedia, ImportMedia,
    EnableHostingOffer, QuoteHosting, PurchaseHosting, RefundHosting, ImportTemplate,
    BrowseSoftware, ReadSoftwareProduct, QuoteSoftware, PurchaseSoftware, PublishSoftware, InstallLicensedSoftware,
    AssignSoftwareSeat, ReleaseSoftwareSeat, ReadSoftwareSource,
    PreviewFirmware, InstallFirmware,
    SearchSoftwareContacts, OfferSoftwareResale, AcceptSoftwareResale,
    DiscardSoftwareCopy,
    ReadServiceGrants, IssueServiceGrants, RevokeServiceGrant,
    DevelopmentContract,
    OpenNativeTools,
    PrepareStockApplication,
    ReadLanguageSymbols,
}

[Serializable, NetSerializable]
public sealed record NCMetaFileSummary(Guid FileId, string Path, long Revision, int Utf8Bytes, string Sha256, bool CanWrite);

[Serializable, NetSerializable]
public sealed record NCMetaFileContent(Guid FileId, long Revision, string Source, string Sha256,
    int Utf8Offset = 0, bool Complete = true, int TotalUtf8Bytes = 0);

[Serializable, NetSerializable]
public sealed record NCMetaProjectSummary(Guid ProjectId, string Name, long Revision);

[Serializable, NetSerializable]
public sealed record NCMetaTemplateSummary(string Id, string Name, string NameRu);

[Serializable, NetSerializable]
public sealed record NCMetaMuiResource(string Path, string Source);

[Serializable, NetSerializable]
public sealed record NCMetaDiagnostic(Guid FileId, long Revision, string Code, string Message, int StartUtf8Byte, int LengthUtf8Bytes);

[Serializable, NetSerializable]
public sealed record NCMetaDeviceSummary(string DeviceId, string Name, string Profile, string Status, bool CanInstall,
    List<string>? Interfaces = null);

[Serializable, NetSerializable]
public sealed record NCMetaLinkSummary(string Slot, string Capability, string ContractHash, List<string> RequiredScopes);

[Serializable, NetSerializable]
public sealed record NCMetaInstallationSummary(Guid Id, Guid ReleaseId, string DeviceId, string Name, string Slot, bool AutoStart);

/// <summary>Only the server creates the actor context. DeviceId and ReleaseId are requested targets, never authority.</summary>
[Serializable, NetSerializable]
public sealed class NCMetaWorkspaceRequestMessage(
    NCMetaWorkspaceOperation operation,
    Guid requestId,
    Guid fileId = default,
    long baseRevision = 0,
    string source = "",
    string deviceId = "",
    Guid releaseId = default,
    int sourceOffset = 0,
    Guid projectId = default,
    string path = "",
    int utf8Offset = 0,
    int chunkBytes = 32 * 1024,
    Guid transferId = default,
    int totalUtf8Bytes = 0,
    string contentSha256 = "",
    bool complete = true,
    Guid installationId = default, NCMetaWorkspacePayload? payload = null) : BoundUserInterfaceMessage
{
    public readonly NCMetaWorkspaceOperation Operation = operation;
    public readonly Guid RequestId = requestId;
    public readonly Guid FileId = fileId;
    public readonly long BaseRevision = baseRevision;
    public readonly string Source = source;
    public readonly string DeviceId = deviceId;
    public readonly Guid ReleaseId = releaseId;
    public readonly int SourceOffset = sourceOffset;
    public readonly Guid ProjectId = projectId;
    public readonly string Path = path;
    public readonly int Utf8Offset = utf8Offset;
    public readonly int ChunkBytes = chunkBytes;
    public readonly Guid TransferId = transferId;
    public readonly int TotalUtf8Bytes = totalUtf8Bytes;
    public readonly string ContentSha256 = contentSha256;
    public readonly bool Complete = complete;
    public readonly Guid InstallationId = installationId;
    public readonly NCMetaWorkspacePayload? Payload = payload;
}

/// <summary>Private editor projection, sent only to the authenticated reader of the physical terminal.</summary>
[Serializable, NetSerializable]
public sealed class NCMetaWorkspaceState : BoundUserInterfaceState
{
    public Guid ProjectId;
    public long ProjectRevision;
    public string ProjectName = "";
    public List<NCMetaProjectSummary> Projects = [];
    public List<NCMetaTemplateSummary> Templates = [];
    public List<NCMetaFileSummary> Files = [];
    public List<NCMetaFileContent> Contents = [];
    public List<NCMetaDiagnostic> Diagnostics = [];
    public List<NCMetaDeviceSummary> Devices = [];
    public List<NCMetaInstallationSummary> Installations = [];
    public List<NCMetaLinkSummary> Links = [];
    public List<string> ApplicationViews = [];
    public string WebsiteAddress = "";
    public NCMetaHostingQuoteSummary? HostingQuote;
    public List<NCMetaHostingLeaseSummary> HostingLeases = [];
    public NCMetaCommerceState? Commerce;
    public NCMetaFirmwarePreview? Firmware;
    public Content.Shared._NC.Meta.Authority.NCMetaGrantPolicySummary? ServiceGrants;
    public NCMetaDevelopmentState? Development;
    public NCMetaNativeToolsState? NativeTools;
    public NCMetaLanguageSymbols? LanguageSymbols;
    public List<string> Logs = [];
    public List<string> DebugVariables = [];
    public Guid ActiveReleaseId;
    public string ActiveReleaseDigest = "";
    public string RequestedScopes = "";
    public string AppContractJson = "";
    public string Notice = "";
    public Guid AcknowledgedRequestId;
    public bool RevisionConflict;
    public bool DebugAttached;
    public bool DebugPaused;
    public bool CanEdit;
    public bool Partial;
    public NCMetaSaveStatus SaveStatus;
}

/// <summary>JSON is limited, schema checked data, never markup or executable code. Patches use the pure MUI patch schema.</summary>
[Serializable, NetSerializable]
public sealed class NCMetaAppState : BoundUserInterfaceState
{
    public Guid SessionId;
    public string ViewId = "";
    public bool Website;
    public string Title = "";
    [NonSerialized] public string Document = "";
    [NonSerialized] public string Styles = "";
    [NonSerialized] public string ContractJson = "";
    public NCMetaMuiPresentation? Presentation;
    [NonSerialized] public List<NCMetaMuiResource> Resources = [];
    [NonSerialized] public string StateJson = "{}";
    public int StateBytes;
    public NCMetaUiValue? Values;
    public long BaseRevision;
    public long Revision;
    public bool FullSnapshot = true;
    public bool Closed;
    public string Notice = "";
}

[Serializable, NetSerializable]
public sealed class NCMetaAppActionMessage(Guid sessionId, string actionId, Guid requestId, long baseRevision, string formJson)
    : BoundUserInterfaceMessage
{
    public readonly Guid SessionId = sessionId;
    public readonly string ActionId = actionId;
    public readonly Guid RequestId = requestId;
    public readonly long BaseRevision = baseRevision;
    public readonly string FormJson = formJson;
}

[Serializable, NetSerializable]
public sealed class NCMetaAppAckMessage(Guid sessionId, long revision) : BoundUserInterfaceMessage
{
    public readonly Guid SessionId = sessionId;
    public readonly long Revision = revision;
}

[Serializable, NetSerializable]
public sealed class NCMetaAppResyncMessage(Guid sessionId, long revision) : BoundUserInterfaceMessage
{
    public readonly Guid SessionId = sessionId;
    public readonly long Revision = revision;
}

[Serializable, NetSerializable]
public sealed class NCMetaAppCloseMessage(Guid sessionId) : BoundUserInterfaceMessage
{
    public readonly Guid SessionId = sessionId;
}

/// <summary>Sent directly to one actor; private projections must never use broadcast SetUiState.</summary>
[Serializable, NetSerializable]
public sealed class NCMetaWorkspaceUpdateMessage(NCMetaWorkspaceState state) : BoundUserInterfaceMessage
{
    public readonly NCMetaWorkspaceState State = state;
}

[Serializable, NetSerializable]
public sealed class NCMetaAppUpdateMessage(NCMetaAppState state) : BoundUserInterfaceMessage
{
    public readonly NCMetaAppState State = state;
}

[Serializable, NetSerializable]
public sealed class NCMetaAppActionResultMessage(Guid sessionId, Guid requestId, long revision, bool pending, bool success, string notice)
    : BoundUserInterfaceMessage
{
    public readonly Guid SessionId = sessionId;
    public readonly Guid RequestId = requestId;
    public readonly long Revision = revision;
    public readonly bool Pending = pending;
    public readonly bool Success = success;
    public readonly string Notice = notice;
}

[Serializable, NetSerializable]
public sealed class NCMetaBrowserNavigateMessage(Guid requestId, string address) : BoundUserInterfaceMessage
{
    public readonly Guid RequestId = requestId;
    public readonly string Address = address;
}

[Serializable, NetSerializable]
public sealed class NCMetaBrowserResultMessage(string notice) : BoundUserInterfaceMessage
{
    public readonly string Notice = notice;
}
