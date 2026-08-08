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
        SubscribeLocalEvent<NCPoliceRecordsConsoleComponent, NCPoliceCreateCaseMessage>(OnCreateCase);
        SubscribeLocalEvent<NCPoliceRecordsConsoleComponent, NCPoliceSelectCaseMessage>(OnSelectCase);
        SubscribeLocalEvent<NCPoliceRecordsConsoleComponent, NCPoliceAddCaseSubjectMessage>(OnAddCaseSubject);
        SubscribeLocalEvent<NCPoliceRecordsConsoleComponent, NCPoliceAddCaseEntryMessage>(OnAddCaseEntry);
        SubscribeLocalEvent<NCPoliceRecordsConsoleComponent, NCPoliceChangeCaseStatusMessage>(OnChangeCaseStatus);
        SubscribeLocalEvent<NCPoliceRecordsConsoleComponent, NCPoliceCreateWarrantMessage>(OnCreateWarrant);
        SubscribeLocalEvent<NCPoliceRecordsConsoleComponent, NCPoliceResolveWarrantMessage>(OnResolveWarrant);
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

    private async void OnCreateCase(EntityUid uid, NCPoliceRecordsConsoleComponent component, NCPoliceCreateCaseMessage args)
    {
        if (!TryGetActionContext(uid, args.Actor, out var user, out var view, out var actorId, out var actorName) ||
            view.Selected is not { } subject || args.Title.Trim().Length is < 1 or > 128 ||
            args.Summary.Trim().Length is < 1 or > 1024)
            return;

        try
        {
            var policeCase = await _database.CreateNCPoliceCaseAsync(
                args.Title, args.Summary, subject, actorId, actorName);
            if (policeCase == null)
                return;

            view.SelectedCaseId = policeCase.Id;
            await RefreshOpenViewsAsync();
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "create case", exception);
        }
    }

    private async void OnSelectCase(EntityUid uid, NCPoliceRecordsConsoleComponent component, NCPoliceSelectCaseMessage args)
    {
        if (args.Actor is not { Valid: true } user || !CanUse(uid, user) ||
            !_views.TryGetValue((uid, user), out var view) || !view.CaseIds.Contains(args.CaseId))
            return;

        view.SelectedCaseId = args.CaseId;
        await SendViewAsync(uid, component, user);
    }

    private async void OnAddCaseSubject(
        EntityUid uid,
        NCPoliceRecordsConsoleComponent component,
        NCPoliceAddCaseSubjectMessage args)
    {
        if (!TryGetActionContext(uid, args.Actor, out var user, out var view, out var actorId, out var actorName) ||
            view.Selected is not { } subject || view.SelectedCaseId != args.CaseId || !Enum.IsDefined(args.Role))
            return;

        try
        {
            if (await _database.AddNCPoliceCaseSubjectAsync(
                    args.CaseId, subject, args.Role, actorId, actorName) == null)
                return;
            await RefreshOpenViewsAsync();
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "add case subject", exception);
        }
    }

    private async void OnAddCaseEntry(
        EntityUid uid,
        NCPoliceRecordsConsoleComponent component,
        NCPoliceAddCaseEntryMessage args)
    {
        var text = args.Text.Trim();
        if (!TryGetActionContext(uid, args.Actor, out var user, out var view, out var actorId, out var actorName) ||
            view.SelectedCaseId != args.CaseId || text.Length is < 1 or > 1024)
            return;

        try
        {
            if (await _database.AddNCPoliceCaseEntryAsync(args.CaseId, text, actorId, actorName) == null)
                return;
            await RefreshOpenViewsAsync();
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "add case report", exception);
        }
    }

    private async void OnChangeCaseStatus(
        EntityUid uid,
        NCPoliceRecordsConsoleComponent component,
        NCPoliceChangeCaseStatusMessage args)
    {
        var reason = args.Reason.Trim();
        if (!TryGetActionContext(uid, args.Actor, out var user, out var view, out var actorId, out var actorName) ||
            view.SelectedCaseId != args.CaseId || !Enum.IsDefined(args.Status) || reason.Length is < 1 or > 1024)
            return;

        try
        {
            if (await _database.SetNCPoliceCaseStatusAsync(
                    args.CaseId, args.Status, reason, actorId, actorName) == null)
                return;
            await RefreshOpenViewsAsync();
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "change case status", exception);
        }
    }

    private async void OnCreateWarrant(
        EntityUid uid,
        NCPoliceRecordsConsoleComponent component,
        NCPoliceCreateWarrantMessage args)
    {
        var reason = args.Reason.Trim();
        if (!TryGetActionContext(uid, args.Actor, out var user, out var view, out var actorId, out var actorName) ||
            view.Selected is not { } target || !Enum.IsDefined(args.Type) || reason.Length is < 1 or > 512 ||
            args.CaseId is { } caseId && view.SelectedCaseId != caseId)
            return;

        try
        {
            if (await _database.CreateNCPoliceWarrantAsync(
                    target, args.CaseId, args.Type, reason, actorId, actorName) == null)
                return;
            await RefreshOpenViewsAsync();
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "create warrant", exception);
        }
    }

    private async void OnResolveWarrant(
        EntityUid uid,
        NCPoliceRecordsConsoleComponent component,
        NCPoliceResolveWarrantMessage args)
    {
        var reason = args.Reason.Trim();
        if (!TryGetActionContext(uid, args.Actor, out var user, out var view, out var actorId, out var actorName) ||
            !view.WarrantIds.Contains(args.WarrantId) || args.Status == NCPoliceWarrantStatus.Active ||
            !Enum.IsDefined(args.Status) || reason.Length is < 1 or > 512)
            return;

        try
        {
            if (await _database.ResolveNCPoliceWarrantAsync(
                    args.WarrantId, args.Status, reason, actorId, actorName) == null)
                return;
            await RefreshOpenViewsAsync();
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "resolve warrant", exception);
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

    private bool TryGetActionContext(
        EntityUid console,
        EntityUid? actor,
        out EntityUid user,
        out ConsoleView view,
        out NCCharacterId actorCharacterId,
        out string actorName)
    {
        user = actor ?? EntityUid.Invalid;
        view = null!;
        actorCharacterId = default;
        actorName = string.Empty;
        if (!user.Valid || !CanUse(console, user) || !_views.TryGetValue((console, user), out var foundView) ||
            !_players.TryGetSessionByEntity(user, out var session) ||
            !_preferences.TryGetSelectedNCCharacterId(session.UserId, out actorCharacterId))
            return false;

        view = foundView;
        actorName = _identity.GetIdentityShortInfo(user, console) ?? MetaData(user).EntityName;
        return true;
    }

    private void HandleDatabaseError(EntityUid console, EntityUid user, string operation, Exception exception)
    {
        Log.Error($"NCPD records failed to {operation} at {console}: {exception}");
        _popup.PopupEntity(Loc.GetString("nc-police-records-error"), console, user);
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

        var cases = await _database.GetNCPoliceCasesAsync(component.CaseListLimit);
        view.CaseIds.Clear();
        foreach (var policeCase in cases)
            view.CaseIds.Add(policeCase.Id);

        NCPoliceCaseData? selectedCase = null;
        if (view.SelectedCaseId is { } selectedCaseId && view.CaseIds.Contains(selectedCaseId))
            selectedCase = await _database.GetNCPoliceCaseAsync(selectedCaseId, component.CaseEntryLimit);
        else
            view.SelectedCaseId = null;

        var warrants = await _database.GetNCPoliceWarrantsAsync(component.WarrantListLimit);
        view.WarrantIds.Clear();
        foreach (var warrant in warrants)
            view.WarrantIds.Add(warrant.Id);

        var summaries = results
            .Select(value => ToSummary(value, _presentThisRound.Contains(value.CharacterId)))
            .OrderByDescending(value => value.PresentThisRound)
            .ThenBy(value => value.CharacterName)
            .ToList();

        _ui.ServerSendUiMessage(uid, NCPoliceRecordsUiKey.Key, new NCPoliceRecordsUpdateMessage(
            summaries,
            selected == null ? null : ToSummary(selected, _presentThisRound.Contains(selected.CharacterId)),
            history.Select(ToHistory).ToList(),
            cases.Select(ToCaseSummary).ToList(),
            selectedCase == null ? null : ToCaseSummary(selectedCase),
            warrants.Select(ToWarrantSummary).ToList(),
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

    private static NCPoliceCaseSummary ToCaseSummary(NCPoliceCaseData value)
    {
        return new NCPoliceCaseSummary(
            value.Id,
            value.Title,
            value.Summary,
            value.Status,
            value.CreatedByName,
            value.CreatedAt,
            value.UpdatedAt,
            value.Subjects.Select(subject => new NCPoliceCaseSubjectSummary(
                subject.CharacterId.Value, subject.CharacterName, subject.Role)).ToList(),
            value.Entries.Select(entry => new NCPoliceCaseEntrySummary(
                entry.Id,
                entry.EntryType,
                entry.Text,
                entry.PreviousStatus,
                entry.NewStatus,
                entry.SubjectCharacterId?.Value,
                entry.SubjectName,
                entry.SubjectRole,
                entry.AuthorName,
                entry.CreatedAt)).ToList());
    }

    private static NCPoliceWarrantSummary ToWarrantSummary(NCPoliceWarrantData value)
    {
        return new NCPoliceWarrantSummary(
            value.Id,
            value.CaseId,
            value.TargetCharacterId.Value,
            value.TargetName,
            value.Type,
            value.Status,
            value.Reason,
            value.IssuedByName,
            value.IssuedAt,
            value.ResolvedByName,
            value.ResolutionReason,
            value.ResolvedAt);
    }

    private sealed class ConsoleView
    {
        public readonly List<NCCharacterId> ResultIds = new();
        public readonly HashSet<long> CaseIds = new();
        public readonly HashSet<long> WarrantIds = new();
        public string Query = string.Empty;
        public NCCharacterId? Selected;
        public long? SelectedCaseId;
    }
}
