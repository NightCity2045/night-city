// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server.Access.Systems;
using Content.Server.Database;
using Content.Server.Popups;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Personnel.Components;
using Content.Shared._NC.Personnel.UI;
using Content.Shared.Access.Systems;
using Content.Shared.GameTicking;
using Content.Shared.IdentityManagement;
using Content.Shared.Roles;
using Robust.Server.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Network;

namespace Content.Server._NC.Personnel;

/// <summary>
/// Server-authoritative personnel workflow shared by every Night City organization.
/// Department prototypes define positions, limits, hierarchy and managers; this system contains no faction rules.
/// </summary>
public sealed partial class NCPersonnelConsoleSystem : EntitySystem
{
    [Dependency] private AccessReaderSystem _accessReader = default!;
    [Dependency] private SharedAccessSystem _access = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IdCardSystem _idCards = default!;
    [Dependency] private IdentitySystem _identity = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    private readonly Dictionary<(EntityUid Console, EntityUid User), PersonnelView> _views = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCPersonnelConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCPersonnelConsoleComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<NCPersonnelConsoleComponent, NCPersonnelSearchMessage>(OnSearch);
        SubscribeLocalEvent<NCPersonnelConsoleComponent, NCPersonnelSelectCharacterMessage>(OnSelect);
        SubscribeLocalEvent<NCPersonnelConsoleComponent, NCPersonnelHireMessage>(OnHire);
        SubscribeLocalEvent<NCPersonnelConsoleComponent, NCPersonnelTerminateMessage>(OnTerminate);
        SubscribeLocalEvent<NCPersonnelConsoleComponent, NCPersonnelChangePositionMessage>(OnChangePosition);
    }

    private async void OnUiOpened(EntityUid uid, NCPersonnelConsoleComponent component, BoundUIOpenedEvent args)
    {
        if (args.Actor is not { Valid: true } user || !_accessReader.IsAllowed(user, uid))
        {
            if (args.Actor is { Valid: true } denied)
                _popup.PopupEntity(Loc.GetString("nc-personnel-access-denied"), uid, denied);
            return;
        }

        _views[(uid, user)] = new PersonnelView();
        await SendViewAsync(uid, component, user);
    }

    private void OnUiClosed(EntityUid uid, NCPersonnelConsoleComponent component, BoundUIClosedEvent args)
    {
        if (args.Actor is { Valid: true } user)
            _views.Remove((uid, user));
    }

    private async void OnSearch(EntityUid uid, NCPersonnelConsoleComponent component, NCPersonnelSearchMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view))
            return;
        var query = args.Query.Trim();
        if (query.Length > component.MaximumSearchLength)
            return;
        view.Query = query;
        await SendViewAsync(uid, component, user);
    }

    private async void OnSelect(EntityUid uid, NCPersonnelConsoleComponent component,
        NCPersonnelSelectCharacterMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view))
            return;
        var characterId = new NCCharacterId(args.CharacterId);
        if (!characterId.IsValid || !view.VisibleCharacters.Contains(characterId))
            return;
        view.Selected = characterId;
        await SendViewAsync(uid, component, user);
    }

    private async void OnHire(EntityUid uid, NCPersonnelConsoleComponent component, NCPersonnelHireMessage args)
    {
        var reason = args.Reason.Trim();
        if (reason.Length == 0)
        {
            if (args.Actor is { Valid: true } actorUser)
                _popup.PopupEntity(Loc.GetString("nc-personnel-reason-missing"), uid, actorUser);
            return;
        }
        ActorContext? manager = null;
        if (args.Actor is { Valid: true } actor)
            manager = await GetActorContextAsync(actor, component.Department);
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            !_prototypes.TryIndex(component.Department, out var department) ||
            !_prototypes.TryIndex<JobPrototype>(args.JobId, out var job) ||
            !department.Roles.Contains(job.ID) || !department.NCPositionLimits.TryGetValue(job.ID, out var limit) ||
            reason.Length is < 1 || reason.Length > component.MaximumReasonLength ||
            view.Selected is not { } target || target.Value != args.CharacterId || !view.CandidateIds.Contains(target))
        {
            return;
        }
        if (manager is not { IsManager: true } context || !CanManageJob(department, context.Job, job.ID))
            return;

        try
        {
            var result = await _database.HireNCCharacterAsync(target, job.ID, limit,
                context.CharacterId, context.Name, reason);
            await HandleChangeResultAsync(uid, user, target, job, result);
            await RefreshOpenViewsAsync();
        }
        catch (Exception exception)
        {
            HandleError(uid, user, "hire", exception);
        }
    }

    private async void OnTerminate(EntityUid uid, NCPersonnelConsoleComponent component,
        NCPersonnelTerminateMessage args)
    {
        var reason = args.Reason.Trim();
        if (reason.Length == 0)
        {
            if (args.Actor is { Valid: true } actorUser)
                _popup.PopupEntity(Loc.GetString("nc-personnel-reason-missing"), uid, actorUser);
            return;
        }
        ActorContext? manager = null;
        if (args.Actor is { Valid: true } actor)
            manager = await GetActorContextAsync(actor, component.Department);
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            !_prototypes.TryIndex(component.Department, out var department) ||
            reason.Length is < 1 || reason.Length > component.MaximumReasonLength ||
            view.Selected is not { } target || target.Value != args.CharacterId || !view.EmployeeJobs.TryGetValue(target, out var targetJob))
        {
            return;
        }
        if (manager is not { IsManager: true } context || !CanManageJob(department, context.Job, targetJob))
            return;

        try
        {
            var jobs = department.Roles.Select(value => value.Id).ToArray();
            var result = await _database.TerminateNCCharacterAsync(target, jobs,
                context.CharacterId, context.Name, reason);
            await HandleChangeResultAsync(uid, user, target,
                _prototypes.Index<JobPrototype>(SharedGameTicker.FallbackOverflowJob), result);
            await RefreshOpenViewsAsync();
        }
        catch (Exception exception)
        {
            HandleError(uid, user, "terminate", exception);
        }
    }

    private async void OnChangePosition(EntityUid uid, NCPersonnelConsoleComponent component,
        NCPersonnelChangePositionMessage args)
    {
        var reason = args.Reason.Trim();
        if (reason.Length == 0)
        {
            if (args.Actor is { Valid: true } actorUser)
                _popup.PopupEntity(Loc.GetString("nc-personnel-reason-missing"), uid, actorUser);
            return;
        }
        ActorContext? manager = null;
        if (args.Actor is { Valid: true } actor)
            manager = await GetActorContextAsync(actor, component.Department);
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            !_prototypes.TryIndex(component.Department, out var department) ||
            !_prototypes.TryIndex<JobPrototype>(args.JobId, out var newJob) ||
            !department.Roles.Contains(newJob.ID) ||
            !department.NCPositionLimits.TryGetValue(newJob.ID, out var limit) ||
            reason.Length is < 1 || reason.Length > component.MaximumReasonLength ||
            view.Selected is not { } target || target.Value != args.CharacterId ||
            !view.EmployeeJobs.TryGetValue(target, out var previousJob) || previousJob == newJob.ID)
            return;
        if (manager is not { IsManager: true } context ||
            !CanManageJob(department, context.Job, previousJob) ||
            !CanManageJob(department, context.Job, newJob.ID) ||
            !department.NCPositionRanks.TryGetValue(previousJob, out var previousRank) ||
            !department.NCPositionRanks.TryGetValue(newJob.ID, out var newRank))
        {
            _popup.PopupEntity(Loc.GetString("nc-personnel-insufficient-authority"), uid, user);
            return;
        }

        var eventType = newRank > previousRank
            ? NCEmploymentEventType.Promoted
            : newRank < previousRank
                ? NCEmploymentEventType.Demoted
                : NCEmploymentEventType.Transferred;
        try
        {
            var jobs = department.Roles.Select(value => value.Id).ToArray();
            var result = await _database.ChangeNCCharacterPositionAsync(target, newJob.ID, limit, jobs,
                eventType, context.CharacterId, context.Name, reason);
            await HandleChangeResultAsync(uid, user, target, newJob, result);
            await RefreshOpenViewsAsync();
        }
        catch (Exception exception)
        {
            HandleError(uid, user, "change position", exception);
        }
    }

    private async Task SendViewAsync(EntityUid uid, NCPersonnelConsoleComponent component, EntityUid user)
    {
        if (Deleted(uid) || Deleted(user) || !_views.TryGetValue((uid, user), out var view) ||
            !_prototypes.TryIndex(component.Department, out var department))
            return;

        var actor = await GetActorContextAsync(user, component.Department);
        var canManage = actor is { IsManager: true };
        var belongsToOrganization = actor is { } organizationActor && department.Roles.Contains(organizationActor.Job);
        var jobIds = department.Roles.Select(value => value.Id).ToArray();
        var roster = belongsToOrganization
            ? await _database.GetNCPersonnelRosterAsync(jobIds)
            : Array.Empty<NCPersonnelEmployeeData>();
        var candidates = canManage
            ? await _database.SearchNCPersonnelCandidatesAsync(view.Query, component.CandidateLimit)
            : Array.Empty<NCPersonnelCandidateData>();

        view.VisibleCharacters.Clear();
        view.CandidateIds.Clear();
        view.EmployeeJobs.Clear();
        foreach (var employee in roster)
        {
            view.VisibleCharacters.Add(employee.CharacterId);
            view.EmployeeJobs[employee.CharacterId] = new ProtoId<JobPrototype>(employee.JobPrototypeId);
        }
        foreach (var candidate in candidates)
        {
            view.VisibleCharacters.Add(candidate.CharacterId);
            view.CandidateIds.Add(candidate.CharacterId);
        }
        if (view.Selected is { } selected && !view.VisibleCharacters.Contains(selected))
            view.Selected = null;

        IReadOnlyList<NCPersonnelHistoryData> history = Array.Empty<NCPersonnelHistoryData>();
        if (view.Selected is { } selectedId)
            history = await _database.GetNCPersonnelHistoryAsync(selectedId, component.HistoryLimit);

        var occupied = roster.GroupBy(value => value.JobPrototypeId)
            .ToDictionary(value => value.Key, value => value.Count());
        var positions = department.Roles
            .Where(value => department.NCPositionLimits.ContainsKey(value))
            .Select(value => new NCPersonnelPositionSummary(value.Id,
                occupied.GetValueOrDefault(value.Id), department.NCPositionLimits[value],
                actor is { IsManager: true } manager && CanManageJob(department, manager.Job, value)))
            .ToList();

        _ui.ServerSendUiMessage(uid, NCPersonnelConsoleUiKey.Key, new NCPersonnelConsoleUpdateMessage(
            department.ID,
            positions,
            roster.Select(value => new NCPersonnelEmployeeSummary(value.CharacterId.Value,
                value.CharacterName, value.JobPrototypeId, value.StartedAt)).ToList(),
            candidates.Select(value => new NCPersonnelCandidateSummary(value.CharacterId.Value,
                value.CharacterName)).ToList(),
            view.Selected?.Value,
            history.Select(value => new NCPersonnelHistorySummary(value.PreviousJobPrototypeId,
                value.NewJobPrototypeId, value.EventType.ToString(), value.Reason,
                value.ActorName, value.CreatedAt)).ToList(),
            canManage), user);
    }

    private async Task<ActorContext?> GetActorContextAsync(EntityUid user, ProtoId<DepartmentPrototype> departmentId)
    {
        if (!_prototypes.TryIndex(departmentId, out var department) ||
            !_players.TryGetSessionByEntity(user, out var session) ||
            !_preferences.TryGetSelectedNCCharacterId(session.UserId, out var actorId))
            return null;
        var activeJob = await _database.GetNCActiveJobAsync(actorId);
        if (activeJob == null || !_prototypes.HasIndex<JobPrototype>(activeJob))
            return null;
        var actorJob = new ProtoId<JobPrototype>(activeJob);
        var actorName = _identity.GetIdentityShortInfo(user, user) ?? MetaData(user).EntityName;
        return new ActorContext(actorId, actorJob, actorName, department.NCPersonnelManagers.Contains(actorJob));
    }

    private static bool CanManageJob(DepartmentPrototype department, ProtoId<JobPrototype>? actorJob,
        ProtoId<JobPrototype> targetJob)
    {
        return actorJob is { } manager && department.NCPersonnelManagers.Contains(manager) &&
               department.NCPositionRanks.TryGetValue(manager, out var managerRank) &&
               department.NCPositionRanks.TryGetValue(targetJob, out var targetRank) && managerRank > targetRank;
    }

    private async Task HandleChangeResultAsync(EntityUid console, EntityUid user, NCCharacterId target,
        JobPrototype resultingJob, NCPersonnelChangeData result)
    {
        if (result.Result != NCPersonnelChangeResult.Success || result.TargetUserId is not { } targetUserId)
        {
            var locale = result.Result switch
            {
                NCPersonnelChangeResult.PositionFull => "nc-personnel-position-full",
                NCPersonnelChangeResult.CharacterUnavailable => "nc-personnel-character-unavailable",
                NCPersonnelChangeResult.CharacterNotFound => "nc-personnel-character-not-found",
                _ => "nc-personnel-change-failed",
            };
            _popup.PopupEntity(Loc.GetString(locale), console, user);
            return;
        }

        await _preferences.RefreshNCEmploymentAsync(targetUserId);
        UpdateLiveId(targetUserId, target, resultingJob);
        _popup.PopupEntity(Loc.GetString("nc-personnel-change-success"), console, user);
    }

    private void UpdateLiveId(NetUserId userId, NCCharacterId target, JobPrototype job)
    {
        if (!_players.TryGetSessionById(userId, out var session) || session.AttachedEntity is not { Valid: true } mob ||
            !_preferences.TryGetSelectedNCCharacterId(userId, out var selected) || selected != target ||
            !_idCards.TryFindIdCard(mob, out var card))
            return;

        // Equipment is handled through RP, but the carried ID immediately receives the authoritative title and access.
        _access.SetAccessToJob(card.Owner, job, false);
        _idCards.TryChangeJobTitle(card.Owner, job.LocalizedName, card.Comp, mob);
        _idCards.TryChangeJobDepartment(card.Owner, job, card.Comp);
        if (_prototypes.Resolve(job.Icon, out var icon))
            _idCards.TryChangeJobIcon(card.Owner, icon, card.Comp, mob);
        card.Comp.JobPrototype = job.ID;
        Dirty(card.Owner, card.Comp);
    }

    private bool TryGetView(EntityUid console, EntityUid? actor, out EntityUid user, out PersonnelView view)
    {
        user = actor ?? EntityUid.Invalid;
        view = null!;
        if (!user.Valid || Deleted(user) || !_views.TryGetValue((console, user), out var foundView))
            return false;
        view = foundView;
        return true;
    }

    private async Task RefreshOpenViewsAsync()
    {
        foreach (var key in _views.Keys.ToArray())
        {
            if (Deleted(key.Console) || Deleted(key.User) ||
                !TryComp<NCPersonnelConsoleComponent>(key.Console, out var component))
            {
                _views.Remove(key);
                continue;
            }
            await SendViewAsync(key.Console, component, key.User);
        }
    }

    private void HandleError(EntityUid console, EntityUid user, string operation, Exception exception)
    {
        Log.Error($"Personnel console failed to {operation} at {console}: {exception}");
        _popup.PopupEntity(Loc.GetString("nc-personnel-change-failed"), console, user);
    }

    private sealed class PersonnelView
    {
        public readonly HashSet<NCCharacterId> VisibleCharacters = new();
        public readonly HashSet<NCCharacterId> CandidateIds = new();
        public readonly Dictionary<NCCharacterId, ProtoId<JobPrototype>> EmployeeJobs = new();
        public string Query = string.Empty;
        public NCCharacterId? Selected;
    }

    private sealed record ActorContext(
        NCCharacterId CharacterId, ProtoId<JobPrototype> Job, string Name, bool IsManager);
}
