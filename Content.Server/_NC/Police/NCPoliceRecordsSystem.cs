// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.CriminalRecords.Systems;
using Content.Server.Database;
using Content.Server.Popups;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Police.Components;
using Content.Shared._NC.Police.UI;
using Content.Shared.Access.Systems;
using Content.Shared.CriminalRecords;
using Content.Shared.GameTicking;
using Content.Shared.IdentityManagement;
using Content.Shared.Security;
using Content.Shared.StationRecords;
using Content.Shared.StationRecords.Components;
using Content.Shared.StationRecords.Systems;
using Content.Shared.Database._NC.Police;
using Robust.Server.GameObjects;
using Robust.Shared.Player;
using System.Linq;
using System.Threading.Tasks;

namespace Content.Server._NC.Police;

/// <summary>
/// Server-authoritative persistent NCPD records terminal and live vanilla status bridge.
/// </summary>
public sealed partial class NCPoliceRecordsSystem : EntitySystem
{
    [Dependency] private AccessReaderSystem _access = default!;
    [Dependency] private CriminalRecordsSystem _criminalRecords = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IdentitySystem _identity = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private StationRecordsSystem _stationRecords = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    private readonly Dictionary<(EntityUid Console, EntityUid User), ConsoleView> _views = new();
    private readonly Dictionary<NCCharacterId, StationRecordKey> _liveRecords = new();
    private readonly HashSet<NCCharacterId> _presentThisRound = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPoliceRecordsConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCPoliceRecordsConsoleComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<NCPoliceRecordsConsoleComponent, NCPoliceRecordsSearchMessage>(OnSearch);
        SubscribeLocalEvent<NCPoliceRecordsConsoleComponent, NCPoliceRecordsSelectMessage>(OnSelect);
        SubscribeLocalEvent<NCPoliceRecordsConsoleComponent, NCPoliceRecordsChangeStatusMessage>(OnChangeStatus);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private async void OnUiOpened(EntityUid uid, NCPoliceRecordsConsoleComponent component, BoundUIOpenedEvent args)
    {
        if (args.Actor is not { Valid: true } user || !CanUse(uid, user))
            return;

        _views[(uid, user)] = new ConsoleView();
        await SendViewAsync(uid, component, user);
    }

    private void OnUiClosed(EntityUid uid, NCPoliceRecordsConsoleComponent component, BoundUIClosedEvent args)
    {
        if (args.Actor is { Valid: true } user)
            _views.Remove((uid, user));
    }

    private async void OnSearch(EntityUid uid, NCPoliceRecordsConsoleComponent component, NCPoliceRecordsSearchMessage args)
    {
        if (args.Actor is not { Valid: true } user || !CanUse(uid, user) ||
            !_views.TryGetValue((uid, user), out var view))
        {
            return;
        }

        var query = args.Query.Trim();
        if (query.Length > component.MaximumSearchLength)
            return;

        try
        {
            view.Query = query;
            view.Selected = null;
            await SendViewAsync(uid, component, user);
        }
        catch (Exception exception)
        {
            Log.Error($"NCPD record search failed at {uid}: {exception}");
            _popup.PopupEntity(Loc.GetString("nc-police-records-error"), uid, user);
        }
    }

    private async void OnSelect(EntityUid uid, NCPoliceRecordsConsoleComponent component, NCPoliceRecordsSelectMessage args)
    {
        if (args.Actor is not { Valid: true } user || !CanUse(uid, user) ||
            !_views.TryGetValue((uid, user), out var view))
        {
            return;
        }

        var selected = new NCCharacterId(args.CharacterId);
        if (!selected.IsValid || !view.ResultIds.Contains(selected))
            return;

        view.Selected = selected;
        await SendViewAsync(uid, component, user);
    }

    private async void OnChangeStatus(
        EntityUid uid,
        NCPoliceRecordsConsoleComponent component,
        NCPoliceRecordsChangeStatusMessage args)
    {
        if (args.Actor is not { Valid: true } user || !CanUse(uid, user) ||
            !_views.TryGetValue((uid, user), out var view))
        {
            return;
        }

        var target = new NCCharacterId(args.CharacterId);
        var reason = args.Reason?.Trim();
        if (!target.IsValid || view.Selected != target ||
            !Enum.IsDefined(args.Status) ||
            string.IsNullOrWhiteSpace(reason) ||
            reason.Length > component.MaximumReasonLength ||
            !_players.TryGetSessionByEntity(user, out var session) ||
            !_preferences.TryGetSelectedNCCharacterId(session.UserId, out var actorCharacterId))
        {
            return;
        }

        var actorName = _identity.GetIdentityShortInfo(user, uid) ?? MetaData(user).EntityName;
        try
        {
            var record = await _database.SetNCPoliceStatusAsync(
                target,
                args.Status,
                reason,
                actorCharacterId,
                actorName);
            if (record == null)
                return;

            UpdateLiveRecord(record);
            await SendViewAsync(uid, component, user);
        }
        catch (Exception exception)
        {
            Log.Error($"NCPD status update failed for character {target.Value}: {exception}");
            _popup.PopupEntity(Loc.GetString("nc-police-records-error"), uid, user);
        }
    }

    private async void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        if (!_preferences.TryGetSelectedNCCharacterId(args.Player.UserId, out var characterId))
            return;

        // Presence means that the character participated in this launch. It intentionally does not reveal connection state.
        _presentThisRound.Add(characterId);

        try
        {
            var record = await _database.GetNCPoliceRecordAsync(characterId);
            if (Deleted(args.Mob) || Deleted(args.Station) ||
                !TryComp<StationRecordsComponent>(args.Station, out var stationRecords) ||
                _stationRecords.GetRecordByName((args.Station, stationRecords), args.Profile.Name) is not { } recordId)
            {
                return;
            }

            var key = new StationRecordKey(recordId, args.Station);
            _liveRecords[characterId] = key;

            // Every spawned character is indexed, even before their first persistent police action.
            // This lets a newly created dossier update the vanilla HUD during the same round.
            if (record != null)
                UpdateLiveRecord(record);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to restore persistent NCPD status for character {characterId.Value}: {exception}");
        }

        await RefreshOpenViewsAsync();
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _views.Clear();
        _liveRecords.Clear();
        _presentThisRound.Clear();
    }

    private bool CanUse(EntityUid console, EntityUid user)
    {
        if (_access.IsAllowed(user, console))
            return true;

        _popup.PopupEntity(Loc.GetString("nc-police-records-access-denied"), console, user);
        return false;
    }

    private async Task SendViewAsync(
        EntityUid uid,
        NCPoliceRecordsConsoleComponent component,
        EntityUid user)
    {
        if (Deleted(uid) || Deleted(user) || !_views.TryGetValue((uid, user), out var view))
            return;

        var results = await _database.SearchNCPoliceRecordsAsync(view.Query, component.SearchResultLimit);
        view.ResultIds.Clear();
        foreach (var result in results)
            view.ResultIds.Add(result.CharacterId);

        NCPoliceRecordData? selected = null;
        IReadOnlyList<NCPoliceRecordEventData> history = Array.Empty<NCPoliceRecordEventData>();
        if (view.Selected is { } selectedId)
        {
            selected = await _database.GetNCPoliceRecordAsync(selectedId);
            history = await _database.GetNCPoliceRecordHistoryAsync(selectedId, component.HistoryLimit);
        }

        var summaries = results
            .Select(value => ToSummary(value, _presentThisRound.Contains(value.CharacterId)))
            .OrderByDescending(value => value.PresentThisRound)
            .ThenBy(value => value.CharacterName)
            .ToList();

        _ui.ServerSendUiMessage(uid, NCPoliceRecordsUiKey.Key, new NCPoliceRecordsUpdateMessage(
            summaries,
            selected == null ? null : ToSummary(selected, _presentThisRound.Contains(selected.CharacterId)),
            history.Select(ToHistory).ToList(),
            true), user);
    }

    private async Task RefreshOpenViewsAsync()
    {
        // Copy the keys because a user can close a console while a database request is in flight.
        foreach (var key in _views.Keys.ToArray())
        {
            if (Deleted(key.Console) || Deleted(key.User) ||
                !TryComp<NCPoliceRecordsConsoleComponent>(key.Console, out var console))
            {
                _views.Remove(key);
                continue;
            }

            await SendViewAsync(key.Console, console, key.User);
        }
    }

    private void UpdateLiveRecord(NCPoliceRecordData recordData)
    {
        if (!_liveRecords.TryGetValue(recordData.CharacterId, out var key) ||
            !_stationRecords.TryGetRecord<CriminalRecord>(key, out var record))
        {
            return;
        }

        var vanillaStatus = ToVanillaStatus(recordData.Status);
        var vanillaReason = vanillaStatus is SecurityStatus.Wanted or SecurityStatus.Suspected or SecurityStatus.Hostile
            ? recordData.Reason
            : null;
        _criminalRecords.OverwriteStatus(
            key,
            record,
            vanillaStatus,
            vanillaReason,
            recordData.UpdatedByName);
    }

    private static SecurityStatus ToVanillaStatus(NCPoliceOperationalStatus status)
    {
        return status switch
        {
            NCPoliceOperationalStatus.Questioning => SecurityStatus.Suspected,
            NCPoliceOperationalStatus.Suspected => SecurityStatus.Suspected,
            NCPoliceOperationalStatus.Wanted => SecurityStatus.Wanted,
            NCPoliceOperationalStatus.Detained => SecurityStatus.Detained,
            NCPoliceOperationalStatus.Arrested => SecurityStatus.Detained,
            NCPoliceOperationalStatus.Imprisoned => SecurityStatus.Detained,
            NCPoliceOperationalStatus.Paroled => SecurityStatus.Paroled,
            NCPoliceOperationalStatus.Released => SecurityStatus.Discharged,
            NCPoliceOperationalStatus.Missing => SecurityStatus.Suspected,
            NCPoliceOperationalStatus.Dangerous => SecurityStatus.Hostile,
            _ => SecurityStatus.None,
        };
    }

    private static NCPoliceRecordSummary ToSummary(NCPoliceRecordData value, bool presentThisRound)
    {
        return new NCPoliceRecordSummary(
            value.CharacterId.Value,
            value.CharacterName,
            value.JobPrototypeId,
            presentThisRound,
            value.Status,
            value.Reason,
            value.UpdatedByName,
            value.UpdatedAt);
    }

    private static NCPoliceRecordHistoryEntry ToHistory(NCPoliceRecordEventData value)
    {
        return new NCPoliceRecordHistoryEntry(
            value.Id,
            value.EventType,
            value.PreviousStatus,
            value.NewStatus,
            value.Reason,
            value.ActorName,
            value.CreatedAt);
    }

    private sealed class ConsoleView
    {
        public readonly List<NCCharacterId> ResultIds = new();
        public string Query = string.Empty;
        public NCCharacterId? Selected;
    }
}
