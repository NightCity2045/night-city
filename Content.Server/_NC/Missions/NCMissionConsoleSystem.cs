// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Content.Shared._NC.Missions;
using Content.Shared._NC.Missions.Components;
using Content.Shared._NC.Missions.Prototypes;
using Content.Shared._NC.Missions.UI;
using Content.Shared._NC.Identity;
using Content.Shared.Administration;
using Content.Shared.Administration.Managers;
using Robust.Server.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Missions;

/// <summary>
/// Server-authoritative terminal for starting one physical public mission in the shared Night City world.
/// Participants are audit/finish authority only; they never form an access whitelist for the deployed zone.
/// </summary>
public sealed partial class NCMissionConsoleSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private City.Zones.NCZoneSystem _zones = default!;
    [Dependency] private NCMissionSystem _missions = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private ISharedAdminManager _admins = default!;

    private readonly Dictionary<(EntityUid Console, EntityUid User), MissionView> _views = [];
    private readonly HashSet<(EntityUid Console, EntityUid User)> _processing = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCMissionConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCMissionConsoleComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<NCMissionConsoleComponent, NCMissionConsoleStartMessage>(OnStart);
        SubscribeLocalEvent<NCMissionConsoleComponent, NCMissionConsoleFinishMessage>(OnFinish);
        SubscribeLocalEvent<NCMissionRunChangedEvent>(OnMissionChanged);
    }

    private void OnUiOpened(EntityUid uid, NCMissionConsoleComponent component, BoundUIOpenedEvent args)
    {
        if (args.Actor is not { Valid: true } user)
            return;

        _views[(uid, user)] = new MissionView();
        SendView(uid, component, user);
    }

    private void OnUiClosed(EntityUid uid, NCMissionConsoleComponent component, BoundUIClosedEvent args)
    {
        if (args.Actor is not { Valid: true } user)
            return;

        _views.Remove((uid, user));
        _processing.Remove((uid, user));
    }

    private async void OnStart(
        EntityUid uid,
        NCMissionConsoleComponent component,
        NCMissionConsoleStartMessage args)
    {
        if (!TryGetOpenView(uid, args.Actor, out var user, out var view) ||
            !CanUseLegacyConsole(user) ||
            !_processing.Add((uid, user)))
            return;

        try
        {
            if (!TryGetCharacterId(user, out var characterId))
            {
                view.Notice = "nc-mission-console-result-no-character";
            }
            else if (!IsConfigurationValid(component) ||
                     _missions.TryGetRunForSlot(component.SlotId, out _))
            {
                view.Notice = "nc-mission-console-result-unavailable";
            }
            else
            {
                var result = await _missions.TryStartRunAsync(
                    component.Definition,
                    [characterId],
                    component.SlotId);
                view.Notice = result.Success
                    ? "nc-mission-console-result-started"
                    : "nc-mission-console-result-failed";
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Mission console {ToPrettyString(uid)} failed to start a public mission: {exception}");
            view.Notice = "nc-mission-console-result-failed";
        }
        finally
        {
            _processing.Remove((uid, user));
            SendView(uid, component, user);
        }
    }

    private void OnFinish(
        EntityUid uid,
        NCMissionConsoleComponent component,
        NCMissionConsoleFinishMessage args)
    {
        if (!TryGetOpenView(uid, args.Actor, out var user, out var view) ||
            !CanUseLegacyConsole(user) ||
            !_processing.Add((uid, user)))
            return;

        try
        {
            if (!TryGetCharacterId(user, out var characterId) ||
                !_missions.TryGetRunForSlot(component.SlotId, out var run) ||
                !run.Participants.Contains(characterId))
            {
                view.Notice = "nc-mission-console-result-not-authorized";
            }
            else if (_missions.TryFinishRun(run.RunId, NCMissionRunResult.Aborted, out _))
            {
                view.Notice = "nc-mission-console-result-finish-requested";
            }
            else
            {
                view.Notice = "nc-mission-console-result-failed";
            }
        }
        finally
        {
            _processing.Remove((uid, user));
            SendView(uid, component, user);
        }
    }

    private void OnMissionChanged(ref NCMissionRunChangedEvent args)
    {
        foreach (var key in _views.Keys)
        {
            if (Deleted(key.Console) || Deleted(key.User) ||
                !TryComp(key.Console, out NCMissionConsoleComponent? component) ||
                component.SlotId != args.SlotId)
                continue;

            SendView(key.Console, component, key.User);
        }
    }

    private void SendView(EntityUid console, NCMissionConsoleComponent component, EntityUid user)
    {
        if (!_views.TryGetValue((console, user), out var view))
            return;

        var hasCharacter = TryGetCharacterId(user, out var characterId);
        var hasRun = _missions.TryGetRunForSlot(component.SlotId, out var run);
        var canFinish = CanUseLegacyConsole(user) && hasCharacter && hasRun && run.State == NCMissionRunState.Active &&
                        run.Participants.Contains(characterId);
        var canStart = CanUseLegacyConsole(user) && hasCharacter && !hasRun && IsConfigurationValid(component) &&
                       !_processing.Contains((console, user));
        var now = DateTime.UtcNow;
        var remainingSeconds = hasRun ? RemainingSeconds(run.ExpiresAt, now) : (int?) null;
        var warningInSeconds = hasRun ? RemainingSeconds(run.WarningAt, now) : (int?) null;

        _ui.ServerSendUiMessage(console, NCMissionConsoleUiKey.Key,
            new NCMissionConsoleUpdateMessage(
                component.Definition.Id,
                component.SlotId,
                hasRun,
                hasRun ? run.State : NCMissionRunState.Closed,
                remainingSeconds,
                warningInSeconds,
                canStart,
                canFinish,
                view.Notice),
            user);
    }

    private bool IsConfigurationValid(NCMissionConsoleComponent component)
    {
        return component.SlotId.IsValid &&
               _zones.TryGetMissionSlot(component.SlotId, out var slot) &&
               _prototypes.TryIndex(component.Definition, out NCMissionDefinitionPrototype? definition) &&
               definition.RunDuration > TimeSpan.Zero &&
               definition.EvacuationTime >= TimeSpan.Zero &&
               definition.EvacuationTime <= definition.RunDuration &&
               _prototypes.TryIndex(definition.Template, out NCZoneTemplatePrototype? template) &&
               template.TargetZoneId == component.SlotId.ZoneId &&
               template.CompatibleZoneKinds.Contains(slot.Kind);
    }

    // Preserve the old deployment diagnostic surface; ordinary work is now issued by physical fixers.
    private bool CanUseLegacyConsole(EntityUid user) => _players.TryGetSessionByEntity(user, out var session) &&
        (_admins.HasAdminFlag(session, AdminFlags.Mapping) || _admins.HasAdminFlag(session, AdminFlags.Server));

    private bool TryGetOpenView(
        EntityUid console,
        EntityUid? actor,
        out EntityUid user,
        out MissionView view)
    {
        user = actor ?? EntityUid.Invalid;
        view = null!;
        if (!user.Valid || Deleted(user) || !_views.TryGetValue((console, user), out var found))
            return false;

        view = found;
        return true;
    }

    private bool TryGetCharacterId(EntityUid user, out NCCharacterId characterId)
    {
        characterId = default;
        return _players.TryGetSessionByEntity(user, out var session) &&
               _preferences.TryGetSelectedNCCharacterId(session.UserId, out characterId);
    }

    private static int RemainingSeconds(DateTime target, DateTime now) =>
        (int) Math.Ceiling((target - now).TotalSeconds);

    private sealed class MissionView
    {
        public string Notice = string.Empty;
    }
}
