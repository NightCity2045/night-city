// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Police;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Admin;

public sealed partial class NCLivingCityAdminSystem
{
    private async Task<InspectionResult> InspectIncidentAsync(string target)
    {
        if (!Guid.TryParse(target, out var raw) || await _incidents.GetAsync(new NCCityIncidentId(raw), true) is not
            { } record) return Fail("incident-not-found");
        var value = record.Snapshot; var fields = new FieldBuilder();
        fields.Add("incident", "id", value.IncidentId); fields.Add("incident", "version", record.AggregateVersion);
        fields.Add("incident", "kind", value.Kind); fields.Add("incident", "severity", value.Severity);
        fields.Add("incident", "stage", value.Stage); fields.Add("incident", "zone", value.LocationZone);
        fields.Add("incident", "participants", value.Participants.Count); fields.Add("incident", "evidence", value.Evidence.Count);
        fields.Add("incident", "witnesses", value.WitnessStatements.Count); fields.Add("incident", "dispatches", value.DispatchIds.Count);
        fields.Add("incident", "escalates-at", value.EscalatesAt?.Elapsed); fields.Add("incident", "outcome", value.Outcome?.OutcomeKey);
        return Ok(fields);
    }

    private async Task<InspectionResult> InspectPoliceWantedAsync(string target)
    {
        if (!TryGuid(target, out var raw))
            return Fail("invalid-wanted-id");
        var record = await _wanted.GetAsync(new NCPoliceWantedId(raw), true);
        if (record == null)
            return Fail("wanted-record-not-found");

        var snapshot = record.Snapshot;
        var fields = new FieldBuilder();
        fields.Add("wanted", "wanted-id", snapshot.WantedId);
        fields.Add("wanted", "subject", snapshot.Subject);
        fields.Add("wanted", "subject-name", snapshot.SubjectName);
        fields.Add("wanted", "status", snapshot.Status);
        fields.Add("wanted", "risk", snapshot.Risk);
        fields.Add("wanted", "reason", snapshot.ReasonKey);
        fields.Add("wanted", "incident-id", snapshot.IncidentId);
        fields.Add("wanted", "source-world-event", snapshot.SourceWorldEventId);
        fields.Add("wanted", "issued-by", snapshot.IssuedBy);
        fields.Add("wanted", "issued-by-name", snapshot.IssuedByName);
        fields.Add("wanted", "issued-at", snapshot.IssuedAt.Elapsed);
        fields.Add("wanted", "updated-at", snapshot.UpdatedAt.Elapsed);
        fields.Add("wanted", "aggregate-version", record.AggregateVersion);
        return Ok(fields);
    }
    private async Task<InspectionResult> InspectCityServiceAsync(string target)
    {
        if (!Guid.TryParse(target, out var raw) || await _cityServices.GetServiceAsync(new NCCityServiceId(raw), true)
            is not { } record) return Fail("city-service-not-found");
        var value = record.Snapshot; var fields = new FieldBuilder();
        fields.Add("service", "id", value.ServiceId); fields.Add("service", "version", record.AggregateVersion);
        fields.Add("service", "key", value.StableKey); fields.Add("service", "kind", value.Kind);
        fields.Add("service", "base-zone", value.BaseZone); fields.Add("service", "teams", value.Capacity.TotalTeams);
        fields.Add("service", "available", value.Capacity.AvailableTeams); fields.Add("service", "queued", value.Queue.Count);
        fields.Add("service", "active", value.ActiveDispatches.Count); return Ok(fields);
    }
    private async Task<InspectionResult> InspectServiceDispatchAsync(string target)
    {
        if (!Guid.TryParse(target, out var raw) || await _cityServices.GetDispatchAsync(new NCServiceDispatchId(raw), true)
            is not { } record) return Fail("service-dispatch-not-found");
        var value = record.Snapshot; var fields = new FieldBuilder();
        fields.Add("dispatch", "id", value.DispatchId); fields.Add("dispatch", "version", record.AggregateVersion);
        fields.Add("dispatch", "service", value.ServiceId); fields.Add("dispatch", "incident", value.IncidentId);
        fields.Add("dispatch", "status", value.Status); fields.Add("dispatch", "destination", value.DestinationZone);
        fields.Add("dispatch", "threat", value.ThreatBasisPoints); fields.Add("dispatch", "eta", value.ExpectedArrivalAt?.Elapsed);
        fields.Add("dispatch", "group", value.ResponseGroupId);
        if (_ncpdServiceDispatch.TryGetSnapshot(value.DispatchId, out var ncpd))
        {
            fields.Add("ncpd-response", "unit", ncpd.UnitId);
            fields.Add("ncpd-response", "command", ncpd.CommandId);
            fields.Add("ncpd-response", "adapter-status", ncpd.AdapterStatus);
            fields.Add("ncpd-response", "updated-at", ncpd.UpdatedAt.Elapsed);
            fields.Add("ncpd-response", "failure", ncpd.Failure.Length == 0 ? "none" : ncpd.Failure);
        }
        return Ok(fields);
    }
    private async Task<InspectionResult> InspectFactionAsync(string target)
    {
        if (!Guid.TryParse(target, out var raw) || await _factions.GetAsync(new NCOrganizationId(raw), true) is not
            { } record) return Fail("faction-not-found");
        var value = record.Snapshot; var fields = new FieldBuilder();
        fields.Add("faction", "id", value.FactionId); fields.Add("faction", "version", record.AggregateVersion);
        fields.Add("faction", "key", value.StableKey); fields.Add("faction", "name", value.DisplayName);
        fields.Add("faction", "territories", value.Territories.Count); fields.Add("faction", "relations", value.Relations.Count);
        fields.Add("faction", "manpower", value.Resources.AvailableManpower); return Ok(fields);
    }
    private InspectionResult InspectDistrictSafety(string target)
    {
        if (!NCZoneId.TryParse(target, out var id) || !_districtSafety.TryGet(id, out var value))
            return Fail("district-safety-not-found");
        var fields = new FieldBuilder(); fields.Add("safety", "district", value.DistrictId);
        fields.Add("safety", "threat", value.Threat); fields.Add("safety", "law-pressure", value.LawPressure);
        fields.Add("safety", "service-load", value.ServiceLoad); fields.Add("safety", "incidents", value.ActiveIncidents);
        fields.Add("safety", "controller", value.ControllingFaction); fields.Add("safety", "revision", value.Revision);
        return Ok(fields);
    }
    private async Task<InspectionResult> InspectCityNewsAsync(string target)
    {
        if (!Guid.TryParse(target, out var raw) || await _cityNews.GetAsync(new NCCityNewsId(raw), true) is not
            { } record) return Fail("city-news-not-found");
        var value = record.Snapshot; var fields = new FieldBuilder(); fields.Add("news", "id", value.NewsId);
        fields.Add("news", "incident", value.IncidentId); fields.Add("news", "kind", value.Kind);
        fields.Add("news", "zone", value.ZoneId); fields.Add("news", "severity", value.Severity);
        fields.Add("news", "headline-key", value.HeadlineKey); fields.Add("news", "retracted", value.Retracted);
        return Ok(fields);
    }
    private async Task<InspectionResult> InspectEstateAsync(string target)
    {
        if (!Guid.TryParse(target, out var raw) || await _estates.GetAsync(new NCEstateId(raw), true) is not
            { } record) return Fail("estate-not-found");
        var value = record.Snapshot; var fields = new FieldBuilder(); fields.Add("estate", "id", value.EstateId);
        fields.Add("estate", "version", record.AggregateVersion); fields.Add("estate", "decedent", value.Decedent);
        fields.Add("estate", "beneficiary", value.Beneficiary); fields.Add("estate", "replacement", value.Replacement);
        fields.Add("estate", "status", value.Status); fields.Add("estate", "assets", value.Assets.Count);
        fields.Add("estate", "claims", value.Claims.Count); return Ok(fields);
    }
    private InspectionResult InspectNeuralRuntime()
    {
        var value = _neural.GetRuntimeSnapshot(); var fields = new FieldBuilder();
        fields.Add("neural", "active", value.Active); fields.Add("neural", "mode", value.BudgetMode);
        fields.Add("neural", "administratively-disabled", value.AdministrativelyDisabled);
        fields.Add("neural", "budget-ready", value.BudgetReady);
        fields.Add("neural", "queued", value.Queued); fields.Add("neural", "in-flight", value.InFlight);
        fields.Add("neural", "input-tokens", value.SessionInputTokens); fields.Add("neural", "output-tokens", value.SessionOutputTokens);
        fields.Add("neural", "cost-usd-micros", value.SessionCostUsdMicros);
        fields.Add("neural", "weekly-period", value.WeeklyPeriodKey);
        fields.Add("neural", "weekly-cost-usd-micros", value.WeeklyCostUsdMicros);
        fields.Add("neural", "requests-social-cognition", value.SocialCognitionRequests);
        fields.Add("neural", "requests-strategic-decision", value.StrategicDecisionRequests);
        fields.Add("neural", "requests-dialogue-continuation", value.DialogueContinuationRequests);
        fields.Add("neural", "requests-memory-summary", value.MemorySummaryRequests);
        fields.Add("neural", "requests-narrative-render", value.NarrativeRenderRequests);
        fields.Add("neural", "accepted", value.Accepted); fields.Add("neural", "fallbacks", value.Fallbacks);
        fields.Add("neural", "coalesced", value.CoalescedRequests);
        fields.Add("neural", "pending-coalesced-followers", value.PendingCoalescedFollowers);
        fields.Add("neural", "stale", value.Stale); fields.Add("neural", "invalid", value.Invalid);
        fields.Add("neural", "provider-failures", value.ProviderFailures);
        fields.Add("neural", "timeouts", value.Timeouts); fields.Add("neural", "rejects", value.ValidationRejects);
        fields.Add("neural", "failures", value.ConsecutiveFailures); fields.Add("neural", "circuit-until", value.CircuitOpenUntil?.Elapsed);
        fields.Add("neural", "last-failure-kind", value.LastFailureKind);
        fields.Add("neural", "last-failure-reason", value.LastFailureReason);
        foreach (var (kind, runtime) in value.KindRuntime)
        {
            var section = NeuralKindSection(kind);
            fields.Add(section, "submitted", runtime.Submitted);
            fields.Add(section, "coalesced", runtime.Coalesced);
            fields.Add(section, "accepted", runtime.Accepted);
            fields.Add(section, "fallback", runtime.Fallback);
            fields.Add(section, "rejected", runtime.Rejected);
            fields.Add(section, "input-tokens", runtime.InputTokens);
            fields.Add(section, "output-tokens", runtime.OutputTokens);
            fields.Add(section, "cost-usd-micros", runtime.CostUsdMicros);
        }
        return Ok(fields);
    }
}
