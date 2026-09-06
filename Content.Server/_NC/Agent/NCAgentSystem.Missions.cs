// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.Missions;
using Content.Shared._NC.Agent.Components;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.Missions.UI;
using Content.Shared.PDA;

namespace Content.Server._NC.Agent;

public sealed partial class NCAgentSystem
{
    [Dependency] private NCFixerMissionSystem _fixerMissions = default!;

    private void InitializeMissions()
    {
        SubscribeLocalEvent<NCAgentComponent, NCFixerWorkActionMessage>(OnFixerWorkAction);
        SubscribeLocalEvent<NCAgentComponent, NCFixerMissionRefreshMessage>(OnFixerWorkRefresh);
        // Old clients cannot bypass contact issuance by replaying the former catalogue commands.
        SubscribeLocalEvent<NCAgentComponent, NCAgentMissionStartMessage>(OnLegacyMissionStart);
        SubscribeLocalEvent<NCAgentComponent, NCAgentMissionFinishMessage>(OnLegacyMissionFinish);
    }
    private void OnLegacyMissionStart(EntityUid uid, NCAgentComponent component, NCAgentMissionStartMessage args) => SendMissionSnapshot(uid, args.Actor);
    private void OnLegacyMissionFinish(EntityUid uid, NCAgentComponent component, NCAgentMissionFinishMessage args) => SendMissionSnapshot(uid, args.Actor);
    private void OnFixerWorkRefresh(EntityUid uid, NCAgentComponent component, NCFixerMissionRefreshMessage args) => SendMissionSnapshot(uid, args.Actor);

    private async void OnFixerWorkAction(EntityUid uid, NCAgentComponent component, NCFixerWorkActionMessage args)
    {
        if (!CanUseFixerWork(uid, args.Actor) || !_processing.Add((uid, args.Actor))) return;
        try
        {
            var success = await _fixerMissions.TryActAsync(args.Actor, args);
            await SendFixerWorkAsync(uid, args.Actor, success ? "nc-fixer-work-updated" : "nc-fixer-work-failed");
        }
        catch (Exception exception) { HandleDatabaseError(uid, args.Actor, "fixer mission", exception); }
        finally { _processing.Remove((uid, args.Actor)); }
    }

    private bool CanUseFixerWork(EntityUid uid, EntityUid user) =>
        TryGetView(uid, user, out _, out var view) && view.ActiveApp == NCAgentApp.Missions &&
        TryGetCharacterId(user, out var character) && character == view.Owner && CanUseCitiNetService(character, out _);

    private void SendMissionSnapshot(EntityUid uid, EntityUid user) => _ = SendFixerWorkAsync(uid, user);
    private async Task SendFixerWorkAsync(EntityUid uid, EntityUid user, string notice = "")
    {
        try
        {
            if (!CanUseFixerWork(uid, user))
            {
                if (!Deleted(uid) && TryGetView(uid, user, out _, out _))
                    _ui.ServerSendUiMessage(uid, PdaUiKey.Key,
                        new NCFixerMissionUpdateMessage([], [], 0, false, "nc-contract-unavailable"), user);
                return;
            }
            var view = await _fixerMissions.GetAgentViewAsync(user, notice);
            if (CanUseFixerWork(uid, user) && TryGetCharacterId(user, out var current) && current.Value == view.Viewer)
                _ui.ServerSendUiMessage(uid, PdaUiKey.Key, view, user);
        }
        catch (Exception exception) { HandleDatabaseError(uid, user, "read fixer missions", exception); }
    }
}
