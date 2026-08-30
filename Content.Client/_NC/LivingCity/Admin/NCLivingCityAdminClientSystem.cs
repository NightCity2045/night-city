// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Admin;

namespace Content.Client._NC.LivingCity.Admin;

/// <summary>
/// Narrow network boundary used by the F7 Living City tab. It never executes console command text.
/// </summary>
public sealed partial class NCLivingCityAdminClientSystem : EntitySystem
{
    public event Action<NCLivingCityAdminOverviewSnapshot>? OverviewReceived;
    public event Action<NCLivingCityAdminObserverSnapshot>? ObserverReceived;
    public event Action<NCLivingCityAdminInspectResult>? InspectionReceived;
    public event Action<NCLivingCityAdminActorActionResult>? ActorActionReceived;
    public event Action<NCLivingCityAdminGroupActionResult>? GroupActionReceived;
    public event Action<NCLivingCityAdminPopulationActionResult>? PopulationActionReceived;
    public event Action<NCLivingCityAdminInfrastructureActionResult>? InfrastructureActionReceived;
    public event Action<NCLivingCityAdminGeographyActionResult>? GeographyActionReceived;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCLivingCityAdminOverviewSnapshot>(OnOverview);
        SubscribeNetworkEvent<NCLivingCityAdminObserverSnapshot>(OnObserver);
        SubscribeNetworkEvent<NCLivingCityAdminInspectResult>(OnInspection);
        SubscribeNetworkEvent<NCLivingCityAdminActorActionResult>(OnActorAction);
        SubscribeNetworkEvent<NCLivingCityAdminGroupActionResult>(OnGroupAction);
        SubscribeNetworkEvent<NCLivingCityAdminPopulationActionResult>(OnPopulationAction);
        SubscribeNetworkEvent<NCLivingCityAdminInfrastructureActionResult>(OnInfrastructureAction);
        SubscribeNetworkEvent<NCLivingCityAdminGeographyActionResult>(OnGeographyAction);
    }

    public Guid RequestOverview(NCLivingCityAdminOverviewScope scope)
    {
        var requestId = Guid.NewGuid();
        RaiseNetworkEvent(new NCLivingCityAdminOverviewRequest(requestId, scope));
        return requestId;
    }

    public Guid RequestObserver(string zoneId, string actorId)
    {
        var requestId = Guid.NewGuid();
        RaiseNetworkEvent(new NCLivingCityAdminObserverRequest(requestId, zoneId, actorId));
        return requestId;
    }

    public Guid RequestInspection(
        NCLivingCityAdminInspectKind kind,
        string target,
        string secondaryTarget)
    {
        var requestId = Guid.NewGuid();
        RaiseNetworkEvent(new NCLivingCityAdminInspectRequest(requestId, kind, target, secondaryTarget));
        return requestId;
    }

    public Guid RequestActorAction(
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
        Guid confirmationToken)
    {
        var requestId = Guid.NewGuid();
        RaiseNetworkEvent(new NCLivingCityAdminActorActionRequest(
            requestId,
            action,
            actorId,
            populationScopeId,
            stableLocalId,
            archetypeId,
            homeZoneId,
            workZoneId,
            fullName,
            targetZoneId,
            priority,
            reason,
            confirmationToken));
        return requestId;
    }

    public Guid RequestGroupAction(
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
        Guid confirmationToken)
    {
        var requestId = Guid.NewGuid();
        RaiseNetworkEvent(new NCLivingCityAdminGroupActionRequest(
            requestId,
            action,
            groupId,
            destinationGroupId,
            groupScopeId,
            stableLocalId,
            displayName,
            profileId,
            factionId,
            actorId,
            leaderActorId,
            successorActorId,
            role,
            currentZoneId,
            purpose,
            currentOrder,
            missionReference,
            incidentId,
            priority,
            reason,
            confirmationToken));
        return requestId;
    }

    public Guid RequestPopulationAction(
        NCLivingCityAdminPopulationAction action,
        string profileId,
        string reason,
        Guid confirmationToken)
    {
        var requestId = Guid.NewGuid();
        RaiseNetworkEvent(new NCLivingCityAdminPopulationActionRequest(
            requestId, action, profileId, reason, confirmationToken));
        return requestId;
    }

    public Guid RequestInfrastructureAction(
        NCLivingCityAdminInfrastructureAction action,
        string targetId,
        bool resetAttempts,
        string reason,
        Guid confirmationToken)
    {
        var requestId = Guid.NewGuid();
        RaiseNetworkEvent(new NCLivingCityAdminInfrastructureActionRequest(
            requestId, action, targetId, resetAttempts, reason, confirmationToken));
        return requestId;
    }

    public Guid RequestGeographyAction(
        NCLivingCityAdminGeographyAction action,
        string zoneId,
        string activityMode,
        string reason,
        Guid confirmationToken)
    {
        var requestId = Guid.NewGuid();
        RaiseNetworkEvent(new NCLivingCityAdminGeographyActionRequest(
            requestId, action, zoneId, activityMode, reason, confirmationToken));
        return requestId;
    }

    private void OnOverview(NCLivingCityAdminOverviewSnapshot snapshot) => OverviewReceived?.Invoke(snapshot);

    private void OnObserver(NCLivingCityAdminObserverSnapshot snapshot) => ObserverReceived?.Invoke(snapshot);

    private void OnInspection(NCLivingCityAdminInspectResult result) => InspectionReceived?.Invoke(result);

    private void OnActorAction(NCLivingCityAdminActorActionResult result) => ActorActionReceived?.Invoke(result);

    private void OnGroupAction(NCLivingCityAdminGroupActionResult result) => GroupActionReceived?.Invoke(result);

    private void OnPopulationAction(NCLivingCityAdminPopulationActionResult result) =>
        PopulationActionReceived?.Invoke(result);

    private void OnInfrastructureAction(NCLivingCityAdminInfrastructureActionResult result) =>
        InfrastructureActionReceived?.Invoke(result);

    private void OnGeographyAction(NCLivingCityAdminGeographyActionResult result) =>
        GeographyActionReceived?.Invoke(result);
}
