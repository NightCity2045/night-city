// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Server.Database;
using Content.Server._NC.Identity;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Persistence.Components;
using Content.Shared._NC.Persistence;
using Content.Shared._NC.RED.Progression;
using Content.Shared._NC.CCVar;
using Robust.Shared.Configuration;
using System.Linq;
using System.Threading.Tasks;

namespace Content.Server._NC.Persistence;

/// <summary>
/// Loads durable character state after the Mind has received a stable ProfileId.
/// </summary>
public sealed partial class CharacterPersistenceSystem : EntitySystem
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private CharacterIdentitySystem _identity = default!;
    [Dependency] private Content.Shared.Mind.SharedMindSystem _mind = default!;

    private ISawmill _log = default!;

    public override void Initialize()
    {
        base.Initialize();
        _log = Logger.GetSawmill("nc.persistence");
        SubscribeLocalEvent<CharacterIdentityLoadedEvent>(OnIdentityLoaded);
        SubscribeNetworkEvent<NCCharacterStateRequest>(OnStateRequest);
    }

    private void OnStateRequest(NCCharacterStateRequest request, EntitySessionEventArgs args)
    {
        var character = args.SenderSession.AttachedEntity;
        if (character == null ||
            !_identity.TryGetIdentity(character.Value, out var profileId, out var accountId) ||
            accountId != args.SenderSession.UserId ||
            !_mind.TryGetMind(character.Value, out var mind, out _))
            return;

        // Re-read the aggregate so a reopened window sees legal, ownership, and HR changes
        // committed after the character originally spawned.
        _ = LoadCharacterAsync(new CharacterIdentityLoadedEvent(
            profileId,
            accountId,
            mind,
            character.Value));
    }

    private void OnIdentityLoaded(ref CharacterIdentityLoadedEvent args)
    {
        _ = LoadCharacterAsync(args);
    }

    private async Task LoadCharacterAsync(CharacterIdentityLoadedEvent args)
    {
        try
        {
            var snapshot = await _database.GetOrCreateNCCharacterAsync(
                args.ProfileId.Value,
                args.AccountId.UserId,
                _configuration.GetCVar(NCCVars.BankCurrency),
                _configuration.GetCVar(NCCVars.BankStartingBalance));

            if (!Exists(args.Mind) || !Exists(args.Character))
                return;

            var state = EnsureComp<CharacterPersistentStateComponent>(args.Mind);
            ApplySnapshot(state, args.ProfileId, snapshot);

            // Downstream systems subscribe to this event instead of depending on async load timing.
            var loaded = new CharacterPersistentStateLoadedEvent(
                args.ProfileId,
                args.AccountId,
                args.Mind,
                args.Character,
                snapshot);
            RaiseLocalEvent(args.Character, ref loaded, true);
        }
        catch (Exception exception)
        {
            _log.Error(
                $"Failed to load persistent state for profile {args.ProfileId}: {exception}");
        }
    }

    private static void ApplySnapshot(
        CharacterPersistentStateComponent state,
        ProfileId profileId,
        NCCharacterSnapshot snapshot)
    {
        state.ProfileId = profileId;
        state.CharacterName = snapshot.CharacterName;
        state.CompletedRounds = snapshot.Progression.CompletedRounds;
        state.Level = snapshot.Progression.Level;
        state.SpentSkillPoints = snapshot.Progression.SpentSkillPoints;
        var activeEmployment = snapshot.Employment is
            { EmploymentState: not (NCEmploymentState.Terminated or NCEmploymentState.Invalid) } employment
            ? employment
            : null;
        state.OrganizationId = activeEmployment?.OrganizationId;
        state.DepartmentId = activeEmployment?.DepartmentId;
        state.PositionId = activeEmployment?.PositionId;
        state.EmploymentState = snapshot.Employment == null
            ? null
            : (byte) snapshot.Employment.EmploymentState;
        state.EmploymentVersion = snapshot.Employment?.Version ?? 0;
        state.PersonalBankAccountId = snapshot.PersonalBankAccount?.BankAccountId;
        state.PersonalBalance = snapshot.PersonalBankAccount?.Balance ?? 0;
        state.PropertyCount = snapshot.Properties.Count;
        state.BusinessCount = snapshot.Businesses.Count;
        state.Properties = snapshot.Properties
            .Select(entry => new NCPropertySummary(
                entry.Property.PrototypeId,
                entry.Property.PropertyType,
                entry.Ownership.ShareBasisPoints,
                (byte) entry.Property.Status))
            .ToList();
        state.Businesses = snapshot.Businesses
            .Select(entry => new NCBusinessSummary(
                entry.Business.BusinessId,
                entry.Business.Name,
                entry.Business.BusinessType,
                entry.Ownership.ShareBasisPoints,
                entry.CoownerCount,
                (byte) entry.Business.Status))
            .ToList();
        state.Licenses = snapshot.Licenses
            .Select(entry => new NCLegalSummary(
                entry.LicensePrototypeId,
                (byte) entry.Status,
                entry.ExpiresAt,
                null))
            .ToList();
        state.Documents = snapshot.Documents
            .Select(entry => new NCLegalSummary(
                entry.DocumentPrototypeId,
                (byte) entry.Status,
                entry.ExpiresAt,
                entry.SerialNumber))
            .ToList();
        state.LifecycleStatus = (byte) snapshot.Lifecycle.Status;
        state.Loaded = true;
    }
}

/// <summary>
/// Raised only after the database aggregate has been loaded successfully.
/// </summary>
[ByRefEvent]
public readonly record struct CharacterPersistentStateLoadedEvent(
    ProfileId ProfileId,
    Robust.Shared.Network.NetUserId AccountId,
    EntityUid Mind,
    EntityUid Character,
    NCCharacterSnapshot Snapshot);
