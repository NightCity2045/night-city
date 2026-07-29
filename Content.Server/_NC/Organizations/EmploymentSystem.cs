// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.Identity;
using Content.Server._NC.Persistence;
using Content.Server._NC.RED.Progression;
using Content.Shared._NC.Organizations;
using Content.Shared._NC.Organizations.Components;
using Content.Shared._NC.Persistence.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Mind;
using Content.Shared.Verbs;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;

namespace Content.Server._NC.Organizations;

/// <summary>
/// Validates online employment actions and immediately applies the resulting access to the target ID card.
/// Database permission checks remain authoritative so crafted network messages cannot bypass hierarchy.
/// </summary>
public sealed partial class EmploymentSystem : EntitySystem
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private CharacterIdentitySystem _identity = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedMindSystem _mind = default!;
    [Dependency] private SharedIdCardSystem _idCard = default!;
    [Dependency] private SharedAccessSystem _access = default!;
    [Dependency] private CharacterProgressionSystem _progression = default!;
    [Dependency] private IPlayerManager _players = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CharacterPersistentStateLoadedEvent>(OnCharacterLoaded);
        SubscribeLocalEvent<NCEmployableComponent, GetVerbsEvent<AlternativeVerb>>(OnGetAlternativeVerbs);
        SubscribeNetworkEvent<NCEmploymentActionRequest>(OnEmploymentAction);
        SubscribeNetworkEvent<NCHROnlineListRequest>(OnOnlineListRequest);
        SubscribeNetworkEvent<NCHROpenFileRequest>(OnOpenFileRequest);
    }

    private void OnCharacterLoaded(ref CharacterPersistentStateLoadedEvent args)
    {
        // The marker keeps verb discovery spatial and event-driven instead of scanning all players.
        EnsureComp<NCEmployableComponent>(args.Character);
        if (args.Snapshot.Employment != null)
            ApplyEmploymentToCharacter(args.Character, args.Snapshot.Employment);
    }

    private void OnGetAlternativeVerbs(
        EntityUid uid,
        NCEmployableComponent component,
        GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess ||
            !_players.TryGetSessionByEntity(args.User, out var session) ||
            !TryGetPersistentState(args.User, out var actorState) ||
            !TryGetPersistentState(uid, out var targetState) ||
            actorState.EmploymentState != (byte) NCEmploymentState.Active ||
            actorState.OrganizationId == null ||
            actorState.PositionId == null ||
            targetState.LifecycleStatus != (byte) NCCharacterLifecycleStatus.Alive)
        {
            return;
        }

        if (!TryGetPosition(actorState.PositionId.Value, out var actorPosition) ||
            !HasAnyHrPermission(actorPosition))
        {
            return;
        }

        // HR staff may inspect unemployed people or members of their own organization only.
        if (targetState.OrganizationId != null &&
            targetState.OrganizationId != actorState.OrganizationId)
        {
            return;
        }

        if (uid == args.User)
        {
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString("nc-hr-roster-verb"),
                Priority = 2,
                Act = () => SendOnlineList(session),
            });
        }

        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("nc-hr-verb"),
            Priority = 1,
            Act = () => OpenHrPanelAsync(uid, session),
        });
    }

    private void OnOnlineListRequest(NCHROnlineListRequest request, EntitySessionEventArgs args)
    {
        SendOnlineList(args.SenderSession);
    }

    private void OnOpenFileRequest(NCHROpenFileRequest request, EntitySessionEventArgs args)
    {
        var target = GetEntity(request.Target);
        if (Exists(target))
            OpenHrPanelAsync(target, args.SenderSession);
    }

    private void SendOnlineList(ICommonSession session)
    {
        if (session.AttachedEntity is not { } actor ||
            !TryGetPersistentState(actor, out var actorState) ||
            actorState.OrganizationId is not { } organizationId ||
            actorState.PositionId is not { } actorPositionId ||
            actorState.EmploymentState != (byte) NCEmploymentState.Active ||
            !TryGetPosition(actorPositionId, out var actorPosition) ||
            !HasAnyHrPermission(actorPosition))
        {
            return;
        }

        var organization = _prototypes.EnumeratePrototypes<NCOrganizationPrototype>()
            .FirstOrDefault(prototype => prototype.OrganizationId == organizationId);
        if (organization == null)
            return;

        var online = new List<NCHROnlineCharacterSummary>();
        foreach (var player in _players.Sessions)
        {
            if (player.Status != SessionStatus.InGame ||
                player.AttachedEntity is not { } character ||
                !TryGetPersistentState(character, out var targetState) ||
                targetState.LifecycleStatus != (byte) NCCharacterLifecycleStatus.Alive ||
                targetState.OrganizationId != null &&
                targetState.OrganizationId != organizationId)
            {
                continue;
            }

            var position = targetState.PositionId is { } targetPositionId &&
                           TryGetPosition(targetPositionId, out var positionPrototype)
                ? positionPrototype.ID
                : null;
            online.Add(new NCHROnlineCharacterSummary(
                GetNetEntity(character),
                MetaData(character).EntityName,
                position,
                targetState.EmploymentState ?? (byte) NCEmploymentState.Terminated));
        }

        RaiseNetworkEvent(
            new NCHROnlineListState(
                organization.ID,
                online.OrderBy(entry => entry.Name).ToArray()),
            session.Channel);
    }

    private async void OnEmploymentAction(NCEmploymentActionRequest request, EntitySessionEventArgs args)
    {
        var actorEntity = args.SenderSession.AttachedEntity;
        var targetEntity = GetEntity(request.Target);
        if (actorEntity == null ||
            !Exists(targetEntity) ||
            !_identity.TryGetIdentity(actorEntity.Value, out var actorProfile, out var actorAccount) ||
            actorAccount != args.SenderSession.UserId ||
            !_identity.TryGetIdentity(targetEntity, out var targetProfile, out var targetAccount))
        {
            RaiseNetworkEvent(new NCEmploymentActionResponse(false, "nc-employment-error-target-offline"),
                args.SenderSession.Channel);
            return;
        }

        if (!TryResolveMutation(request, actorProfile.Value, targetProfile.Value, out var mutation, out var error))
        {
            RaiseNetworkEvent(new NCEmploymentActionResponse(false, error), args.SenderSession.Channel);
            return;
        }

        var result = await _database.ApplyNCEmploymentActionAsync(mutation);
        if (result.Success && result.Employment != null && Exists(targetEntity))
        {
            ApplyEmploymentToCharacter(targetEntity, result.Employment);
            if (_mind.TryGetMind(targetEntity, out var mindId, out _) &&
                TryComp<CharacterPersistentStateComponent>(mindId, out var state))
            {
                var employed = result.Employment.EmploymentState is not
                    (NCEmploymentState.Terminated or NCEmploymentState.Invalid);
                state.OrganizationId = employed ? result.Employment.OrganizationId : null;
                state.DepartmentId = employed ? result.Employment.DepartmentId : null;
                state.PositionId = employed ? result.Employment.PositionId : null;
                state.EmploymentState = (byte) result.Employment.EmploymentState;
                state.EmploymentVersion = result.Employment.Version;
                var changed = new NCEmploymentChangedEvent(
                    targetProfile.Value,
                    mindId,
                    result.Employment);
                RaiseLocalEvent(targetEntity, ref changed, true);
                _progression.SendState(targetAccount, state);
            }
        }

        RaiseNetworkEvent(new NCEmploymentActionResponse(result.Success, result.Error),
            args.SenderSession.Channel);

        if (result.Success)
            await SendHrPanelStateAsync(targetEntity, args.SenderSession);
    }

    private async void OpenHrPanelAsync(EntityUid target, ICommonSession session)
    {
        await SendHrPanelStateAsync(target, session);
    }

    private async Task SendHrPanelStateAsync(EntityUid target, ICommonSession session)
    {
        if (!Exists(target) ||
            session.AttachedEntity is not { } actor ||
            !TryGetPersistentState(actor, out var actorState) ||
            !TryGetPersistentState(target, out var targetState) ||
            actorState.EmploymentState != (byte) NCEmploymentState.Active ||
            actorState.OrganizationId == null ||
            actorState.PositionId == null ||
            !TryGetPosition(actorState.PositionId.Value, out var actorPosition) ||
            !HasAnyHrPermission(actorPosition) ||
            targetState.LifecycleStatus != (byte) NCCharacterLifecycleStatus.Alive ||
            targetState.OrganizationId != null &&
            targetState.OrganizationId != actorState.OrganizationId ||
            !_identity.TryGetIdentity(target, out var targetProfile, out _))
        {
            return;
        }

        var historyRows = await _database.GetNCEmploymentHistoryAsync(targetProfile.Value, 25);
        if (!Exists(target) || session.Status != SessionStatus.InGame)
            return;

        var positionsById = _prototypes.EnumeratePrototypes<NCPositionPrototype>()
            .ToDictionary(position => position.PositionId, position => position.ID);
        var history = historyRows
            .Select(entry => new NCEmploymentHistorySummary(
                entry.Action.ToString(),
                entry.OldPositionId is { } oldId && positionsById.TryGetValue(oldId, out var oldPrototype)
                    ? oldPrototype
                    : null,
                entry.NewPositionId is { } newId && positionsById.TryGetValue(newId, out var newPrototype)
                    ? newPrototype
                    : null,
                entry.Reason,
                entry.Timestamp))
            .ToList();

        var organization = _prototypes.EnumeratePrototypes<NCOrganizationPrototype>()
            .FirstOrDefault(prototype => prototype.OrganizationId == actorState.OrganizationId.Value);
        if (organization == null)
            return;

        var currentPosition = targetState.PositionId is { } positionId &&
                              TryGetPosition(positionId, out var targetPosition)
            ? targetPosition.ID
            : null;

        RaiseNetworkEvent(new NCHRPanelState(
                GetNetEntity(target),
                MetaData(target).EntityName,
                organization.ID,
                currentPosition,
                targetState.EmploymentState ?? (byte) NCEmploymentState.Terminated,
                actorPosition.CanHire,
                actorPosition.CanPromote,
                actorPosition.CanDemote,
                actorPosition.CanTransfer,
                actorPosition.CanSuspend,
                actorPosition.CanDismiss,
                actorPosition.MaxPromotableRankWeight,
                targetState.EmploymentVersion,
                history.ToArray()),
            session.Channel);
    }

    private bool TryResolveMutation(
        NCEmploymentActionRequest request,
        int actorProfileId,
        int targetProfileId,
        out NCEmploymentMutation mutation,
        out string? error)
    {
        mutation = default!;
        error = null;

        NCOrganizationPrototype? organization = null;
        NCPositionPrototype? position = null;
        if (request.OrganizationPrototypeId != null &&
            !_prototypes.TryIndex(request.OrganizationPrototypeId, out organization))
        {
            error = "nc-employment-error-invalid-organization";
            return false;
        }

        if (request.PositionPrototypeId != null &&
            !_prototypes.TryIndex(request.PositionPrototypeId, out position))
        {
            error = "nc-employment-error-invalid-position";
            return false;
        }

        if (organization == null && position != null)
            organization = _prototypes.Index(position.Organization);
        if (organization == null)
        {
            error = "nc-employment-error-invalid-organization";
            return false;
        }

        mutation = new NCEmploymentMutation(
            targetProfileId,
            actorProfileId,
            null,
            (NCEmploymentAction) request.Action,
            organization.OrganizationId,
            position?.PositionId,
            request.Reason.Trim(),
            _ticker.RoundId,
            request.PaidSuspension,
            request.ExpectedVersion,
            request.RequestId);
        return true;
    }

    private void ApplyEmploymentToCharacter(EntityUid character, NCCharacterEmployment employment)
    {
        var position = _prototypes.EnumeratePrototypes<NCPositionPrototype>()
            .FirstOrDefault(p => p.PositionId == employment.PositionId);
        if (position == null || !_idCard.TryFindIdCard(character, out var card))
            return;

        var active = employment.EmploymentState == NCEmploymentState.Active;
        _access.TrySetTags(card.Owner, active ? position.Access : []);
        _idCard.TryChangeJobTitle(card.Owner, active ? Loc.GetString(position.Name) : null);
    }

    private bool TryGetPersistentState(
        EntityUid character,
        [NotNullWhen(true)] out CharacterPersistentStateComponent? state)
    {
        state = null;
        return _mind.TryGetMind(character, out var mindId, out _) &&
               TryComp(mindId, out state);
    }

    private static bool HasAnyHrPermission(NCPositionPrototype position)
    {
        return position.CanHire ||
               position.CanPromote ||
               position.CanDemote ||
               position.CanTransfer ||
               position.CanSuspend ||
               position.CanDismiss;
    }

    private bool TryGetPosition(
        Guid positionId,
        [NotNullWhen(true)] out NCPositionPrototype? position)
    {
        position = _prototypes.EnumeratePrototypes<NCPositionPrototype>()
            .FirstOrDefault(prototype => prototype.PositionId == positionId);
        return position != null;
    }
}

[ByRefEvent]
public readonly record struct NCEmploymentChangedEvent(
    int ProfileId,
    EntityUid Mind,
    NCCharacterEmployment Employment);
