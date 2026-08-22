// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.City.Zones;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.CityServices;
using Content.Shared._NC.LivingCity.Social;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.CityServices;

/// <summary>
/// Idempotently creates lore factions as persistent organization parties at active-round start. It never spawns an
/// entity and performs no per-frame work.
/// </summary>
public sealed partial class NCFactionBootstrapSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCFactionRegistrySystem _factions = default!;

    private Task? _inFlight;
    private int _generation;
    private bool _active;

    public bool InitialLoadComplete { get; private set; }
    public int ProfileCount { get; private set; }
    public int CreatedCount { get; private set; }
    public int ExistingCount { get; private set; }
    public int FailedCount { get; private set; }
    public string? ConfigurationError { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(_ => Reset());
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(_ => Reset());
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        Reset();
        _active = true;
        var generation = _generation;
        _inFlight = BootstrapAsync(generation);
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        _active = false;
        _generation++;
        if (_inFlight != null)
            args.AddPendingWrite(_inFlight);
    }

    private async Task BootstrapAsync(int generation)
    {
        try
        {
            var profiles = _prototypes.EnumeratePrototypes<NCFactionProfilePrototype>()
                .OrderBy(static value => value.ID, StringComparer.Ordinal)
                .ToArray();
            ProfileCount = profiles.Length;
            if (!ValidateProfiles(profiles, out var error))
            {
                ConfigurationError = error;
                Log.Error($"Faction bootstrap is disabled: {error}");
                return;
            }

            var identities = profiles.ToDictionary(
                static value => value.ID,
                static value => OrganizationId(value.StableKey),
                StringComparer.Ordinal);
            var accounts = new Dictionary<string, NCAccountId>(StringComparer.Ordinal);

            // Register every party first so cross-faction relations never point at an unresolved organization.
            foreach (var profile in profiles)
            {
                if (!StillActive(generation))
                    return;
                var party = NCPersistentIdentity.Organization(profile.StableKey);
                var registered = await _finance.EnsureOrganizationAsync(
                    profile.StableKey,
                    profile.DisplayName,
                    "faction-bootstrap");
                var account = registered.IsResolved
                    ? await _finance.EnsurePrimaryAccountWithOpeningBalanceAsync(
                        party,
                        profile.StartingFunds,
                        "faction-bootstrap")
                    : registered;
                if (!account.IsResolved)
                {
                    FailedCount++;
                    Log.Error($"Failed to register faction {profile.ID}: {account.Error}");
                    continue;
                }

                accounts[profile.ID] = account.AccountId;
            }

            foreach (var profile in profiles)
            {
                if (!StillActive(generation))
                    return;
                if (!accounts.TryGetValue(profile.ID, out var accountId) ||
                    !_clock.TryGetCurrentTime(out _, out var now))
                    continue;

                var factionId = identities[profile.ID];
                var party = NCPartyRef.ForOrganization(factionId);
                var snapshot = new NCFactionSnapshot(
                    factionId,
                    party,
                    profile.StableKey,
                    profile.DisplayName,
                    profile.IdeologyKeys.ToArray(),
                    new NCFactionPolicy(
                        profile.TaxBasisPoints,
                        profile.ProtectionBasisPoints,
                        profile.PatrolPriority,
                        profile.SupplyPriority,
                        profile.EnforcesLaw,
                        profile.UsesCovertOperations),
                    profile.Territories.Select(value => new NCFactionTerritoryStake(
                        value.Zone,
                        value.Influence,
                        value.ClaimsControl,
                        now)).ToArray(),
                    profile.Relations.Select(value => new NCFactionRelation(
                        identities[value.Faction.Id],
                        value.Status,
                        value.Trust,
                        value.Hostility,
                        null)).ToArray(),
                    new NCFactionResources(accountId, [], profile.AvailableManpower, 0,
                        Array.Empty<NCSocialObligationId>()),
                    1,
                    Guid.NewGuid());
                var result = await _factions.CreateAsync(new NCCreateFactionRequest(
                    NCPersistentIdentity.Command("faction-bootstrap", "create", profile.StableKey),
                    snapshot,
                    NCPersistentIdentity.Correlation("faction-bootstrap", "create", profile.StableKey),
                    AuditSource: "faction-bootstrap"));
                switch (result.Status)
                {
                    case NCFactionMutationStatus.Applied:
                        CreatedCount++;
                        break;
                    case NCFactionMutationStatus.Duplicate:
                        ExistingCount++;
                        break;
                    default:
                        FailedCount++;
                        Log.Error($"Failed to bootstrap faction {profile.ID}: {result.Error}");
                        break;
                }
            }

            InitialLoadComplete = StillActive(generation) && FailedCount == 0;
        }
        catch (Exception exception)
        {
            FailedCount++;
            ConfigurationError = exception.Message;
            Log.Error($"Faction bootstrap failed: {exception}");
        }
    }

    private bool ValidateProfiles(IReadOnlyList<NCFactionProfilePrototype> profiles, out string? error)
    {
        var stableKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = profiles.Select(static value => value.ID).ToHashSet(StringComparer.Ordinal);
        foreach (var profile in profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.StableKey) || profile.StableKey.Length > 96 ||
                !stableKeys.Add(profile.StableKey) || string.IsNullOrWhiteSpace(profile.DisplayName) ||
                profile.DisplayName.Length > 128 || profile.IdeologyKeys.Count is < 1 or > 32 ||
                profile.StartingFunds < 0 || profile.AvailableManpower < 0 ||
                profile.TaxBasisPoints is < 0 or > 10_000 ||
                profile.ProtectionBasisPoints is < 0 or > 10_000)
            {
                error = $"Faction profile {profile.ID} has invalid identity, policy, or resources.";
                return false;
            }

            if (profile.Territories.Any(value => !_zones.TryGetZone(value.Zone, out _) ||
                    value.Influence is < 0 or > 1000) ||
                profile.Relations.Any(value => !ids.Contains(value.Faction.Id) ||
                    value.Faction.Id == profile.ID || value.Trust is < -1000 or > 1000 ||
                    value.Hostility is < 0 or > 1000))
            {
                error = $"Faction profile {profile.ID} references an invalid territory or relation.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private bool StillActive(int generation) => _active && generation == _generation;

    private void Reset()
    {
        _active = false;
        _generation++;
        _inFlight = null;
        InitialLoadComplete = false;
        ProfileCount = 0;
        CreatedCount = 0;
        ExistingCount = 0;
        FailedCount = 0;
        ConfigurationError = null;
    }

    private static NCOrganizationId OrganizationId(string stableKey) =>
        new(NCPersistentIdentity.Organization(stableKey).PersistentId);
}
