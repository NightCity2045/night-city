// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.Missions;
using Content.Shared._NC.Agent.Components;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Missions;
using Content.Shared._NC.Missions.Prototypes;
using Content.Shared.PDA;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Agent;

public sealed partial class NCAgentSystem
{
    [Dependency] private NCMissionSystem _missions = default!;

    private void InitializeMissions()
    {
        SubscribeLocalEvent<NCAgentComponent, NCAgentMissionStartMessage>(OnMissionStart);
        SubscribeLocalEvent<NCAgentComponent, NCAgentMissionFinishMessage>(OnMissionFinish);
        SubscribeLocalEvent<NCMissionRunChangedEvent>(OnMissionRunChanged);
    }

    private async void OnMissionStart(EntityUid uid, NCAgentComponent component, NCAgentMissionStartMessage args)
    {
        await TryStartAgentMissionAsync(uid, args.Actor, args.DefinitionId);
    }

    /// <summary>Starts a published combat recipe through the existing persistent mission allocator.</summary>
    public async Task<bool> TryStartAgentMissionAsync(EntityUid uid, EntityUid user,
        ProtoId<NCMissionDefinitionPrototype> definitionId)
    {
        if (!TryGetView(uid, user, out _, out var view) ||
            view.ActiveApp != NCAgentApp.Missions || !_processing.Add((uid, user)))
            return false;

        var notice = "nc-agent-missions-failed";
        try
        {
            // Authentication, holder identity and network are checked again for every mutation.
            if (CanUseMissionCatalog(uid, user, out notice) &&
                _prototypes.TryIndex(definitionId, out var definition) &&
                definition.AgentAvailable && CanStartAgentMission(definition, out _, out notice) &&
                TryGetCharacterId(user, out var character))
            {
                SendMissionSnapshot(uid, user);
                var result = await _missions.TryStartRunAsync(definition.ID, [character]);
                notice = result.Success ? "nc-agent-missions-started" : "nc-agent-missions-failed";
                return result.Success;
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Agent mission start failed: {exception}");
            notice = "nc-agent-missions-failed";
        }
        finally
        {
            _processing.Remove((uid, user));
            SendMissionSnapshot(uid, user, notice);
        }
        return false;
    }

    private void OnMissionFinish(EntityUid uid, NCAgentComponent component, NCAgentMissionFinishMessage args)
    {
        TryFinishAgentMission(uid, args.Actor, new NCMissionRunId(args.RunId));
    }

    /// <summary>Cancels only a run belonging to the authenticated character.</summary>
    public bool TryFinishAgentMission(EntityUid uid, EntityUid user, NCMissionRunId runId)
    {
        if (!CanUseMissionCatalog(uid, user, out _) ||
            !TryGetCharacterId(user, out var character) ||
            !_missions.TryGetRun(runId, out var run) ||
            !run.Participants.Contains(character) ||
            !_prototypes.TryIndex<NCMissionDefinitionPrototype>(run.DefinitionId, out var definition) ||
            !definition.AgentAvailable)
            return false;

        // The run token prevents a stale cancel button from closing a newer deployment in the same slot.
        var success = _missions.TryFinishRun(run.RunId, NCMissionRunResult.Aborted, out _);
        SendMissionSnapshot(uid, user, success ? "nc-agent-missions-cancelled" : "nc-agent-missions-failed");
        return success;
    }

    private bool CanUseMissionCatalog(EntityUid agent, EntityUid? actor, out string reason)
    {
        reason = "nc-agent-missions-owner-required";
        if (!TryGetView(agent, actor, out var user, out var view) ||
            view.ActiveApp != NCAgentApp.Missions ||
            !TryGetCharacterId(user, out var character) || character != view.Owner)
            return false;

        reason = "nc-agent-missions-offline";
        if (!CanUseCitiNetService(view.Owner, out _))
            return false;
        reason = string.Empty;
        return true;
    }

    private bool CanStartAgentMission(NCMissionDefinitionPrototype definition,
        out NCMissionSlotId slot, out string reason)
    {
        slot = default;
        reason = "nc-agent-missions-unavailable";
        if (definition.Name == null || definition.Description == null || definition.RewardDescription == null ||
            definition.Difficulty is not { } difficulty || !_prototypes.HasIndex(difficulty) ||
            definition.RecommendedPlayers <= 0 || definition.MaximumParticipants <= 0 ||
            definition.RunDuration <= TimeSpan.Zero || definition.EvacuationTime < TimeSpan.Zero ||
            definition.EvacuationTime > definition.RunDuration ||
            !_prototypes.TryIndex(definition.Template, out var template))
            return false;

        slot = new NCMissionSlotId(template.TargetZoneId);
        if (!_zones.TryGetMissionSlot(slot, out var zone) || !template.CompatibleZoneKinds.Contains(zone.Kind))
            return false;
        reason = "nc-agent-missions-busy";
        if (_missions.TryGetRunForSlot(slot, out _))
            return false;
        reason = string.Empty;
        return true;
    }

    private void SendMissionSnapshot(EntityUid agent, EntityUid user, string notice = "")
    {
        if (Deleted(agent) || !TryGetView(agent, user, out _, out var view) ||
            view.ActiveApp != NCAgentApp.Missions)
            return;

        var entries = new List<NCAgentMissionEntry>();
        if (CanUseMissionCatalog(agent, user, out var failure))
        {
            foreach (var definition in _prototypes.EnumeratePrototypes<NCMissionDefinitionPrototype>()
                         .Where(value => value.AgentAvailable)
                         .OrderBy(value => value.Difficulty is { } tier && _prototypes.TryIndex(tier, out var difficulty)
                             ? difficulty.Order : int.MaxValue).ThenBy(value => value.ID))
            {
                var available = CanStartAgentMission(definition, out var slot, out var reason);
                var hasRun = _missions.TryGetRunForSlot(slot, out var run) && run.DefinitionId == definition.ID;
                var location = _zones.TryGetMissionSlot(slot, out var zone) ? zone.Name : string.Empty;
                entries.Add(new NCAgentMissionEntry(definition.ID, location,
                    available && !_processing.Contains((agent, user)), reason,
                    hasRun ? run.RunId.Value : null, hasRun ? run.State : null,
                    hasRun ? Math.Max(0, (int) Math.Ceiling((run.ExpiresAt - DateTime.UtcNow).TotalSeconds)) : null,
                    hasRun && run.State == NCMissionRunState.Active && run.Participants.Contains(view.Owner)));
            }
        }
        else
            notice = failure;

        _ui.ServerSendUiMessage(agent, PdaUiKey.Key, new NCAgentMissionsUpdateMessage(entries, notice), user);
    }

    private void OnMissionRunChanged(ref NCMissionRunChangedEvent args)
    {
        // Refresh only open Agent views; mission lifecycle remains owned by NCMissionSystem.
        foreach (var key in _views.Keys.ToArray())
            SendMissionSnapshot(key.Agent, key.User);
    }
}
