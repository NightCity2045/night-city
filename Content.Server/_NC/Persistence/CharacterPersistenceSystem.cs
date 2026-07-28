using Content.Server.Database;
using Content.Server._NC.Identity;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Persistence.Components;
using Content.Shared._NC.CCVar;
using Robust.Shared.Configuration;
using System.Threading.Tasks;

namespace Content.Server._NC.Persistence;

/// <summary>
/// Loads durable character state after the Mind has received a stable ProfileId.
/// </summary>
public sealed partial class CharacterPersistenceSystem : EntitySystem
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IConfigurationManager _configuration = default!;

    private ISawmill _log = default!;

    public override void Initialize()
    {
        base.Initialize();
        _log = Logger.GetSawmill("nc.persistence");
        SubscribeLocalEvent<CharacterIdentityLoadedEvent>(OnIdentityLoaded);
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
        state.CompletedRounds = snapshot.Progression.CompletedRounds;
        state.Level = snapshot.Progression.Level;
        state.SpentSkillPoints = snapshot.Progression.SpentSkillPoints;
        state.OrganizationId = snapshot.Employment?.OrganizationId;
        state.DepartmentId = snapshot.Employment?.DepartmentId;
        state.PositionId = snapshot.Employment?.PositionId;
        state.PersonalBankAccountId = snapshot.PersonalBankAccount?.BankAccountId;
        state.PersonalBalance = snapshot.PersonalBankAccount?.Balance ?? 0;
        state.PropertyCount = snapshot.Properties.Count;
        state.BusinessCount = snapshot.Businesses.Count;
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
