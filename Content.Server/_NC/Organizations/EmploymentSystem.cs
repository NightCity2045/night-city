using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.Identity;
using Content.Server._NC.Persistence;
using Content.Server._NC.RED.Progression;
using Content.Shared._NC.Organizations;
using Content.Shared._NC.Persistence.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Mind;
using Robust.Shared.Prototypes;
using System.Linq;

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

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CharacterPersistentStateLoadedEvent>(OnCharacterLoaded);
        SubscribeNetworkEvent<NCEmploymentActionRequest>(OnEmploymentAction);
    }

    private void OnCharacterLoaded(ref CharacterPersistentStateLoadedEvent args)
    {
        if (args.Snapshot.Employment != null)
            ApplyEmploymentToCharacter(args.Character, args.Snapshot.Employment);
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
                state.OrganizationId = result.Employment.OrganizationId;
                state.DepartmentId = result.Employment.DepartmentId;
                state.PositionId = result.Employment.PositionId;
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
}

[ByRefEvent]
public readonly record struct NCEmploymentChangedEvent(
    int ProfileId,
    EntityUid Mind,
    NCCharacterEmployment Employment);
