// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Social;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Social;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.CityServices;

/// <summary>Projects canonical incidents into public news and participant-owned knowledge.</summary>
public sealed partial class NCCityIncidentInformationBridgeSystem : EntitySystem
{
    [Dependency] private NCCityNewsRegistrySystem _news = default!;
    [Dependency] private NCRumorRegistrySystem _rumors = default!;
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityIncidentPersistedEvent>(OnIncident);
    }
    private void OnIncident(NCCityIncidentPersistedEvent args)
    {
        if (args.Incident.Snapshot.Stage is NCCityIncidentStage.Verified or NCCityIncidentStage.Resolved)
            _ = _news.PublishIncidentAsync(args.Incident,
                args.Incident.Snapshot.Stage == NCCityIncidentStage.Resolved ? NCCityNewsKind.Resolution :
                    NCCityNewsKind.Incident, "nc-city-news-incident-headline", "nc-city-news-incident-body");
        if (args.Incident.Snapshot.Stage == NCCityIncidentStage.Reported)
            _ = TeachAsync(args.Incident);
    }
    private async Task TeachAsync(NCCityIncidentRecord incident)
    {
        var topic = new ProtoId<NCRumorTopicPrototype>(incident.Snapshot.Kind is NCCityIncidentKind.Violence or
            NCCityIncidentKind.Cyberpsychosis or NCCityIncidentKind.GangActivity ? "NCRumorViolentIncident" :
            "NCRumorRoutineEvent");
        var subject = new NCInformationSubject(NCInformationSubjectKind.Incident,
            incident.Snapshot.IncidentId.ToString());
        foreach (var value in incident.Snapshot.Participants.Where(value => value.Party.IsValid && value.Role is
            NCIncidentParticipantRole.Witness or NCIncidentParticipantRole.Reporter or NCIncidentParticipantRole.Victim))
        {
            var stable = $"{incident.Snapshot.IncidentId}.{value.Party}";
            await _rumors.LearnAsync(new NCLearnInformationRequest(
                NCPersistentIdentity.Command("incident-rumor", "learn", stable), value.Party, topic, subject,
                [incident.Snapshot.LastAppliedEventId], NCRumorSourceKind.DirectWitness, value.Party, null, null,
                value.Party, NCPersistentIdentity.Correlation("incident-rumor", "learn", stable),
                incident.Snapshot.LastAppliedEventId, "incident-information-bridge"));
        }
    }
}
