// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.CityServices;

/// <summary>Creates a city response only from a committed authoritative Humanity transition.</summary>
public sealed partial class NCCyberpsychosisIncidentBridgeSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCHumanityCrisisPersistedEvent>(OnHumanityChanged);
    }

    private void OnHumanityChanged(NCHumanityCrisisPersistedEvent args)
    {
        if (args.PreviousStage == NCHumanityCrisisStage.Cyberpsychosis ||
            args.Humanity.Snapshot.CrisisStage != NCHumanityCrisisStage.Cyberpsychosis)
            return;
        var subject = NCPartyRef.ForActor(args.Humanity.Snapshot.ActorId);
        RaiseLocalEvent(new NCCityIncidentSourceEvent(args.SourceWorldEventId, "humanity",
            "cyberpsychosis-threshold-crossed", args.Humanity.Snapshot.ActorId.ToString(),
            NCCityIncidentKind.Cyberpsychosis, 900, args.CurrentZone,
            [new NCIncidentParticipant(subject, NCIncidentParticipantRole.Subject, true)], args.Initiator,
            NCIncidentMaterializationPolicy.MaterializeForResponse));
    }
}
