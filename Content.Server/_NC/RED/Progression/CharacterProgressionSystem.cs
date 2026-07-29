// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server._NC.Identity;
using Content.Server._NC.Persistence;
using Content.Shared._NC.Persistence.Components;
using Content.Shared._NC.RED.Progression;
using Content.Shared._NC.Organizations;
using Content.Shared.Mind;
using Robust.Server.Player;
using Robust.Shared.Prototypes;
using System.Linq;

namespace Content.Server._NC.RED.Progression;

/// <summary>
/// Server-authoritative RED level and skill allocation API.
/// </summary>
public sealed partial class CharacterProgressionSystem : EntitySystem
{
    private static readonly ProtoId<NCRedProgressionPrototype> DefaultProgression = "NCDefaultProgression";

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private CharacterIdentitySystem _identity = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private SharedMindSystem _mind = default!;

    private readonly Dictionary<int, Dictionary<string, int>> _skills = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CharacterPersistentStateLoadedEvent>(OnCharacterLoaded);
        SubscribeNetworkEvent<NCAllocateSkillRequest>(OnAllocateSkill);
    }

    private void OnCharacterLoaded(ref CharacterPersistentStateLoadedEvent args)
    {
        _skills[args.ProfileId.Value] = args.Snapshot.Skills
            .ToDictionary(skill => skill.SkillPrototypeId, skill => skill.Rank);

        if (TryComp<CharacterPersistentStateComponent>(args.Mind, out var state))
            SendState(args.AccountId, state);
    }

    private async void OnAllocateSkill(NCAllocateSkillRequest request, EntitySessionEventArgs args)
    {
        var attached = args.SenderSession.AttachedEntity;
        if (attached == null ||
            !_identity.TryGetIdentity(attached.Value, out var profileId, out var accountId) ||
            accountId != args.SenderSession.UserId ||
            !_prototypes.TryIndex<NCRedSkillPrototype>(request.SkillPrototypeId, out var skill) ||
            !_prototypes.TryIndex(DefaultProgression, out var progression) ||
            !_skills.TryGetValue(profileId.Value, out var ranks))
        {
            return;
        }

        var result = await _database.SpendNCSkillPointsAsync(
            profileId.Value,
            skill.ID,
            request.TargetRank,
            skill.MaxRank,
            skill.CostPerRank,
            progression.SkillPointsPerLevel,
            request.RequestId,
            _ticker.RoundId);

        if (!Exists(attached.Value) ||
            !_identity.TryGetIdentity(attached.Value, out var currentProfileId, out _) ||
            currentProfileId != profileId ||
            !_mind.TryGetMind(attached.Value, out var mindId, out _) ||
            !TryComp<CharacterPersistentStateComponent>(mindId, out var state))
        {
            return;
        }

        if (result.Success)
        {
            ranks[skill.ID] = result.NewRank;
            state.SpentSkillPoints = result.SpentSkillPoints;
        }

        SendState(accountId, state, result.Error);
    }

    public void SendState(
        Robust.Shared.Network.NetUserId accountId,
        CharacterPersistentStateComponent state,
        string? error = null)
    {
        if (!_players.TryGetSessionById(accountId, out var session) ||
            !_prototypes.TryIndex<NCRedProgressionPrototype>(DefaultProgression, out var progression))
        {
            return;
        }

        _skills.TryGetValue(state.ProfileId.Value, out var ranks);
        var positionPrototypeId = state.PositionId == null
            ? null
            : _prototypes.EnumeratePrototypes<NCPositionPrototype>()
                .FirstOrDefault(position => position.PositionId == state.PositionId)?.ID;
        var organizationPrototypeId = state.OrganizationId == null
            ? null
            : _prototypes.EnumeratePrototypes<NCOrganizationPrototype>()
                .FirstOrDefault(organization =>
                    organization.OrganizationId == state.OrganizationId)?.ID;
        var departmentPrototypeId = state.DepartmentId == null
            ? null
            : _prototypes.EnumeratePrototypes<NCDepartmentPrototype>()
                .FirstOrDefault(department =>
                    department.DepartmentId == state.DepartmentId)?.ID;
        RaiseNetworkEvent(new NCProgressionStateEvent(
            state.CompletedRounds,
            state.Level,
            state.SpentSkillPoints,
            progression.GetTotalSkillPoints(state.Level),
            ranks ?? new Dictionary<string, int>(),
            state.PersonalBalance,
            state.PropertyCount,
            state.BusinessCount,
            state.CharacterName,
            organizationPrototypeId,
            departmentPrototypeId,
            positionPrototypeId,
            state.Properties.ToArray(),
            state.Businesses.ToArray(),
            state.Licenses.ToArray(),
            state.Documents.ToArray(),
            state.LifecycleStatus,
            error), session.Channel);
    }
}
