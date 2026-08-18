// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.Bank;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.GameTicking;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Bank;

/// <summary>
/// Player-facing bank facade over the canonical double-entry ledger. The relational row retains only login
/// credentials for the ATM; its historical Balance column is never read as financial authority.
/// </summary>
public sealed partial class NCBankSystem : EntitySystem
{
    private static readonly ProtoId<NCBankConfigurationPrototype> DefaultConfiguration = "Default";

    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCPartyResolverSystem _parties = default!;

    public NCBankConfigurationPrototype Configuration => _prototypes.Index(DefaultConfiguration);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawnComplete);
    }

    private async void OnPlayerSpawnComplete(PlayerSpawnCompleteEvent args)
    {
        if (!_preferences.TryGetSelectedNCCharacterId(args.Player.UserId, out var characterId))
            return;

        try
        {
            var account = await EnsureAccountAsync(characterId);
            if (account == null)
                return;

            // Credentials are sent only to the owning session; balances are fetched from the common ledger.
            _chat.DispatchServerMessage(
                args.Player,
                Loc.GetString(
                    "nc-bank-account-created",
                    ("account", account.AccountNumber),
                    ("pin", account.Pin)));
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to initialize bank account for character {characterId.Value}: {exception}");
        }
    }

    public async Task<NCBankAccountData?> GetAccountAsync(NCCharacterId characterId)
    {
        var credentials = await _database.GetNCBankCredentialAsync(characterId);
        var account = await _finance.GetPrimaryAccountAsync(NCPartyRef.ForCharacter(characterId));
        return credentials == null || account == null
            ? null
            : ToAccount(credentials, account.Snapshot.Balance);
    }

    public async Task<NCBankAccountData?> AuthenticateAsync(string accountNumber, string pin)
    {
        var credentials = await _database.AuthenticateNCBankCredentialAsync(accountNumber, pin);
        return credentials == null ? null : await GetAccountAsync(credentials.CharacterId);
    }

    public async Task<NCOrganizationBankAccountData?> EnsureOrganizationAccountAsync(
        string organizationPrototypeId,
        int startingBalance)
    {
        if (startingBalance < 0)
            return null;
        var owner = NCPersistentIdentity.Organization(organizationPrototypeId);
        var registered = await _finance.EnsureOrganizationAsync(
            organizationPrototypeId,
            organizationPrototypeId,
            "organization-account-open");
        if (!registered.IsResolved)
            return null;

        var opened = await _finance.EnsurePrimaryAccountWithOpeningBalanceAsync(
            owner,
            startingBalance,
            "organization-account-open");
        return opened.Account == null
            ? null
            : new NCOrganizationBankAccountData(organizationPrototypeId, opened.Account.Snapshot.Balance);
    }

    public async Task<IReadOnlyList<NCOrganizationBankTransactionData>> GetOrganizationTransactionsAsync(
        string organizationPrototypeId,
        int limit)
    {
        var owner = NCPersistentIdentity.Organization(organizationPrototypeId);
        var history = await _finance.GetTransactionHistoryAsync(owner, null, Math.Clamp(limit, 1, 200));
        if (!history.IsValid)
            return [];

        var result = new List<NCOrganizationBankTransactionData>(history.Entries.Count);
        foreach (var entry in history.Entries)
        {
            var leg = entry.Transaction.Snapshot.Legs.Single(value => value.AccountId == history.AccountId);
            var initiator = entry.Transaction.Snapshot.Initiator;
            var identity = await _parties.ResolveAsync(initiator);
            result.Add(new NCOrganizationBankTransactionData(
                entry.AccountAggregateVersion,
                entry.Transaction.Snapshot.Kind == NCLedgerTransactionKind.Salary
                    ? NCOrganizationBankTransactionType.Salary
                    : leg.Amount > 0
                        ? NCOrganizationBankTransactionType.Deposit
                        : NCOrganizationBankTransactionType.Withdrawal,
                Math.Abs(leg.Amount),
                leg.BalanceAfter,
                initiator.TryGetCharacter(out var characterId) ? characterId.Value : null,
                identity.IsResolved ? identity.DisplayName : initiator.ToString(),
                entry.Transaction.Snapshot.Reference,
                entry.RecordedAt));
        }
        return result;
    }

    public async Task<NCSalaryPaymentData> TryPaySalaryAsync(
        NCCharacterId characterId,
        string organizationPrototypeId,
        int startingBalance,
        int salary,
        string jobPrototypeId)
    {
        if (!characterId.IsValid || salary <= 0 ||
            await EnsureAccountAsync(characterId) == null ||
            await EnsureOrganizationAccountAsync(organizationPrototypeId, startingBalance) == null)
            return new(NCSalaryPaymentResult.InvalidRequest, 0, null);

        var employee = NCPartyRef.ForCharacter(characterId);
        var employer = NCPersistentIdentity.Organization(organizationPrototypeId);
        var transactionId = NCTransactionId.New();
        var request = new NCPartyTransferRequest(
            Guid.NewGuid(),
            transactionId,
            employer,
            employee,
            salary,
            NCLedgerTransactionKind.Salary,
            jobPrototypeId.Trim(),
            employer,
            Guid.NewGuid(),
            AuditSource: "organization-payday");
        NCEconomyMutationResult? mutation = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            // A failed command receipt is immutable. Keep the transaction identity stable for exactly-once posting,
            // but use a fresh command identity after an explicit optimistic conflict.
            mutation = await _finance.TransferAsync(request with
            {
                CommandId = attempt == 0 ? request.CommandId : Guid.NewGuid(),
            });
            if (mutation.Status != NCEconomyMutationStatus.VersionConflict)
                break;
        }

        var organization = await _finance.GetPrimaryAccountAsync(employer, true);
        var character = await _finance.GetPrimaryAccountAsync(employee, true);
        return mutation?.Status switch
        {
            NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate =>
                new(NCSalaryPaymentResult.Success, organization?.Snapshot.Balance ?? 0,
                    character?.Snapshot.Balance),
            NCEconomyMutationStatus.InsufficientFunds =>
                new(NCSalaryPaymentResult.InsufficientOrganizationFunds, organization?.Snapshot.Balance ?? 0,
                    character?.Snapshot.Balance),
            _ => new(NCSalaryPaymentResult.InvalidRequest, organization?.Snapshot.Balance ?? 0,
                character?.Snapshot.Balance),
        };
    }

    private async Task<NCBankAccountData?> EnsureAccountAsync(NCCharacterId characterId)
    {
        var configuration = Configuration;
        // The historical numeric column is seeded with zero and ignored; this row represents credentials only.
        var credentials = await _database.GetOrCreateNCBankCredentialAsync(
            characterId,
            configuration.AccountPrefix.ToUpperInvariant(),
            configuration.PinDigits);
        if (credentials == null)
            return null;

        var opened = await _finance.EnsurePrimaryAccountWithOpeningBalanceAsync(
            NCPartyRef.ForCharacter(characterId),
            configuration.StartingBalance,
            "character-account-open");
        return opened.Account == null ? null : ToAccount(credentials, opened.Account.Snapshot.Balance);
    }

    private static NCBankAccountData ToAccount(NCBankCredentialData credentials, long balance)
    {
        return new NCBankAccountData(
            credentials.CharacterId,
            credentials.AccountNumber,
            credentials.Pin,
            balance);
    }
}
