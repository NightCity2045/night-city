// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Admin;

[Serializable, NetSerializable]
public enum NCLivingCityAdminInspectKind : byte
{
    Actor,
    Cognition,
    ActorMaterializationRequest,
    ActorDematerializationRequest,
    ActorRelevance,
    Group,
    GroupMembership,
    GroupPhysicalizationRequest,
    GroupFormation,
    ZoneState,
    Party,
    Account,
    Transaction,
    Employment,
    Relationship,
    Reputation,
    Memory,
    Property,
    PropertyContents,
    PoliceFine,
    OutboxMessage,
    CityZone,
    CityConnectivityZone,
    CityConnectivityValidation,
    SocialProfile,
    RumorBook,
    SocialObligation,
    Conversation,
    EconomicStockNode,
    EconomicCommodityBatch,
    EconomicItem,
    EconomicQuote,
    EconomicReservation,
    EconomicTransfer,
    EconomicFulfillment,
    EconomicReconciliation,
    MarketListing,
    MarketRequest,
    Business,
    ProductionJob,
    Shipment,
    Incident,
    CityService,
    ServiceDispatch,
    Faction,
    DistrictSafety,
    CityNews,
    Estate,
    NeuralRuntime,
}

[Serializable, NetSerializable]
public enum NCLivingCityAdminActorAction : byte
{
    Create,
    Freeze,
    Unfreeze,
    Move,
    CaptureDomains,
    RestoreHealth,
    Materialize,
    Dematerialize,
}

[Serializable, NetSerializable]
public enum NCLivingCityAdminGroupAction : byte
{
    Create,
    AddMember,
    RemoveMember,
    TransferMember,
    SetLeader,
    SetDirective,
    Materialize,
    Dematerialize,
}

[Serializable, NetSerializable]
public enum NCLivingCityAdminPopulationAction : byte
{
    ValidateProfile,
    ImportProfile,
    RebuildZoneStates,
}

[Serializable, NetSerializable]
public enum NCLivingCityAdminInfrastructureAction : byte
{
    RequeueOutboxMessage,
}

[Serializable, NetSerializable]
public enum NCLivingCityAdminGeographyAction : byte
{
    SetZoneActivity,
}

[Serializable, NetSerializable]
public enum NCLivingCityAdminMutationStatus : byte
{
    ConfirmationRequired,
    Applied,
    Duplicate,
    Rejected,
    Unauthorized,
    RateLimited,
    Failed,
}

/// <summary>
/// One redacted display value. Section and key are stable machine-oriented identifiers localized by the client when a
/// known label exists; Value is bounded server-rendered scalar data, never an aggregate payload.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCLivingCityAdminField(
    string Section,
    string Key,
    string Value);

[Serializable, NetSerializable]
public sealed class NCLivingCityAdminOverviewRequest(Guid requestId) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
}

[Serializable, NetSerializable]
public sealed class NCLivingCityAdminOverviewSnapshot(
    Guid requestId,
    bool authorized,
    NCLivingCityAdminField[] fields,
    string? error) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
    public bool Authorized { get; } = authorized;
    public NCLivingCityAdminField[] Fields { get; } = fields;
    public string? Error { get; } = error;
}

[Serializable, NetSerializable]
public sealed class NCLivingCityAdminInspectRequest(
    Guid requestId,
    NCLivingCityAdminInspectKind kind,
    string target,
    string secondaryTarget) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
    public NCLivingCityAdminInspectKind Kind { get; } = kind;
    public string Target { get; } = target;
    public string SecondaryTarget { get; } = secondaryTarget;
}

[Serializable, NetSerializable]
public sealed class NCLivingCityAdminInspectResult(
    Guid requestId,
    NCLivingCityAdminInspectKind kind,
    bool authorized,
    bool success,
    NCLivingCityAdminField[] fields,
    string? error) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
    public NCLivingCityAdminInspectKind Kind { get; } = kind;
    public bool Authorized { get; } = authorized;
    public bool Success { get; } = success;
    public NCLivingCityAdminField[] Fields { get; } = fields;
    public string? Error { get; } = error;
}

/// <summary>
/// Typed actor administration request. Empty ConfirmationToken asks for a short-lived exact-request confirmation; a
/// non-empty token attempts the operation. The server never accepts command text from this contract.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCLivingCityAdminActorActionRequest(
    Guid requestId,
    NCLivingCityAdminActorAction action,
    string actorId,
    string populationScopeId,
    string stableLocalId,
    string archetypeId,
    string homeZoneId,
    string workZoneId,
    string fullName,
    string targetZoneId,
    int priority,
    string reason,
    Guid confirmationToken) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
    public NCLivingCityAdminActorAction Action { get; } = action;
    public string ActorId { get; } = actorId;
    public string PopulationScopeId { get; } = populationScopeId;
    public string StableLocalId { get; } = stableLocalId;
    public string ArchetypeId { get; } = archetypeId;
    public string HomeZoneId { get; } = homeZoneId;
    public string WorkZoneId { get; } = workZoneId;
    public string FullName { get; } = fullName;
    public string TargetZoneId { get; } = targetZoneId;
    public int Priority { get; } = priority;
    public string Reason { get; } = reason;
    public Guid ConfirmationToken { get; } = confirmationToken;
}

[Serializable, NetSerializable]
public sealed class NCLivingCityAdminActorActionResult(
    Guid requestId,
    NCLivingCityAdminActorAction action,
    NCLivingCityAdminMutationStatus status,
    Guid confirmationToken,
    NCLivingCityAdminField[] fields,
    string? error) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
    public NCLivingCityAdminActorAction Action { get; } = action;
    public NCLivingCityAdminMutationStatus Status { get; } = status;
    public Guid ConfirmationToken { get; } = confirmationToken;
    public NCLivingCityAdminField[] Fields { get; } = fields;
    public string? Error { get; } = error;
}

/// <summary>
/// Typed group administration request. All values are bounded and parsed by the authoritative server handler; no
/// console command line or arbitrary mutation payload crosses the network boundary.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCLivingCityAdminGroupActionRequest(
    Guid requestId,
    NCLivingCityAdminGroupAction action,
    string groupId,
    string destinationGroupId,
    string groupScopeId,
    string stableLocalId,
    string displayName,
    string profileId,
    string factionId,
    string actorId,
    string leaderActorId,
    string successorActorId,
    string role,
    string currentZoneId,
    string purpose,
    string currentOrder,
    string missionReference,
    string incidentId,
    int priority,
    string reason,
    Guid confirmationToken) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
    public NCLivingCityAdminGroupAction Action { get; } = action;
    public string GroupId { get; } = groupId;
    public string DestinationGroupId { get; } = destinationGroupId;
    public string GroupScopeId { get; } = groupScopeId;
    public string StableLocalId { get; } = stableLocalId;
    public string DisplayName { get; } = displayName;
    public string ProfileId { get; } = profileId;
    public string FactionId { get; } = factionId;
    public string ActorId { get; } = actorId;
    public string LeaderActorId { get; } = leaderActorId;
    public string SuccessorActorId { get; } = successorActorId;
    public string Role { get; } = role;
    public string CurrentZoneId { get; } = currentZoneId;
    public string Purpose { get; } = purpose;
    public string CurrentOrder { get; } = currentOrder;
    public string MissionReference { get; } = missionReference;
    public string IncidentId { get; } = incidentId;
    public int Priority { get; } = priority;
    public string Reason { get; } = reason;
    public Guid ConfirmationToken { get; } = confirmationToken;
}

[Serializable, NetSerializable]
public sealed class NCLivingCityAdminGroupActionResult(
    Guid requestId,
    NCLivingCityAdminGroupAction action,
    NCLivingCityAdminMutationStatus status,
    Guid confirmationToken,
    NCLivingCityAdminField[] fields,
    string? error) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
    public NCLivingCityAdminGroupAction Action { get; } = action;
    public NCLivingCityAdminMutationStatus Status { get; } = status;
    public Guid ConfirmationToken { get; } = confirmationToken;
    public NCLivingCityAdminField[] Fields { get; } = fields;
    public string? Error { get; } = error;
}

[Serializable, NetSerializable]
public sealed class NCLivingCityAdminPopulationActionRequest(
    Guid requestId,
    NCLivingCityAdminPopulationAction action,
    string profileId,
    string reason,
    Guid confirmationToken) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
    public NCLivingCityAdminPopulationAction Action { get; } = action;
    public string ProfileId { get; } = profileId;
    public string Reason { get; } = reason;
    public Guid ConfirmationToken { get; } = confirmationToken;
}

[Serializable, NetSerializable]
public sealed class NCLivingCityAdminPopulationActionResult(
    Guid requestId,
    NCLivingCityAdminPopulationAction action,
    NCLivingCityAdminMutationStatus status,
    Guid confirmationToken,
    NCLivingCityAdminField[] fields,
    string? error) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
    public NCLivingCityAdminPopulationAction Action { get; } = action;
    public NCLivingCityAdminMutationStatus Status { get; } = status;
    public Guid ConfirmationToken { get; } = confirmationToken;
    public NCLivingCityAdminField[] Fields { get; } = fields;
    public string? Error { get; } = error;
}

[Serializable, NetSerializable]
public sealed class NCLivingCityAdminInfrastructureActionRequest(
    Guid requestId,
    NCLivingCityAdminInfrastructureAction action,
    string targetId,
    bool resetAttempts,
    string reason,
    Guid confirmationToken) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
    public NCLivingCityAdminInfrastructureAction Action { get; } = action;
    public string TargetId { get; } = targetId;
    public bool ResetAttempts { get; } = resetAttempts;
    public string Reason { get; } = reason;
    public Guid ConfirmationToken { get; } = confirmationToken;
}

[Serializable, NetSerializable]
public sealed class NCLivingCityAdminInfrastructureActionResult(
    Guid requestId,
    NCLivingCityAdminInfrastructureAction action,
    NCLivingCityAdminMutationStatus status,
    Guid confirmationToken,
    NCLivingCityAdminField[] fields,
    string? error) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
    public NCLivingCityAdminInfrastructureAction Action { get; } = action;
    public NCLivingCityAdminMutationStatus Status { get; } = status;
    public Guid ConfirmationToken { get; } = confirmationToken;
    public NCLivingCityAdminField[] Fields { get; } = fields;
    public string? Error { get; } = error;
}

[Serializable, NetSerializable]
public sealed class NCLivingCityAdminGeographyActionRequest(
    Guid requestId,
    NCLivingCityAdminGeographyAction action,
    string zoneId,
    string activityMode,
    string reason,
    Guid confirmationToken) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
    public NCLivingCityAdminGeographyAction Action { get; } = action;
    public string ZoneId { get; } = zoneId;
    public string ActivityMode { get; } = activityMode;
    public string Reason { get; } = reason;
    public Guid ConfirmationToken { get; } = confirmationToken;
}

[Serializable, NetSerializable]
public sealed class NCLivingCityAdminGeographyActionResult(
    Guid requestId,
    NCLivingCityAdminGeographyAction action,
    NCLivingCityAdminMutationStatus status,
    Guid confirmationToken,
    NCLivingCityAdminField[] fields,
    string? error) : EntityEventArgs
{
    public Guid RequestId { get; } = requestId;
    public NCLivingCityAdminGeographyAction Action { get; } = action;
    public NCLivingCityAdminMutationStatus Status { get; } = status;
    public Guid ConfirmationToken { get; } = confirmationToken;
    public NCLivingCityAdminField[] Fields { get; } = fields;
    public string? Error { get; } = error;
}
