// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.Missions;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.Missions;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.CityServices;

/// <summary>Turns failed or aborted authored mission runs into the same incident pipeline as every other domain.</summary>
public sealed partial class NCMissionIncidentBridgeSystem : EntitySystem
{
    [Dependency] private NCMissionSystem _missions = default!;
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCMissionRunChangedEvent>(OnRunChanged);
    }
    private void OnRunChanged(ref NCMissionRunChangedEvent args)
    {
        if (!_missions.TryGetRunForSlot(args.SlotId, out var run) || run.State is not
            (NCMissionRunState.Failed or NCMissionRunState.Aborted) || run.Participants.Count == 0)
            return;
        var initiator = NCPartyRef.ForCharacter(run.Participants.OrderBy(value => value.Value).First());
        var participants = run.Participants.Select(value => new NCIncidentParticipant(NCPartyRef.ForCharacter(value),
            NCIncidentParticipantRole.Subject, true)).ToArray();
        RaiseLocalEvent(new NCCityIncidentSourceEvent(
            NCPersistentIdentity.Command("mission", "terminal-event", $"{run.RunId}.{run.State}"), "mission",
            run.State.ToString().ToLowerInvariant(), run.RunId.ToString(), NCCityIncidentKind.Violence,
            run.Result == NCMissionRunResult.Failure ? 600 : 350, args.SlotId.ZoneId, participants,
            initiator, NCIncidentMaterializationPolicy.MaterializeForResponse));
    }
}
