// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Threading;
using System.Threading.Tasks;
using Content.Server.Chat.Managers;
using Content.Server.Database;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.Bank;
using Content.Shared._NC.Identity;
using Content.Shared.GameTicking;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Bank;

/// <summary>
/// Server authority for character-owned bank accounts.
/// A single gate serializes balance mutations and prevents concurrent withdrawals from overspending an account.
/// </summary>
public sealed partial class NCBankSystem : EntitySystem
{
    private static readonly ProtoId<NCBankConfigurationPrototype> DefaultConfiguration = "Default";

    [Dependency] private IChatManager _chat = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;

    private readonly SemaphoreSlim _transactionGate = new(1, 1);

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

            // Credentials are sent only to the owning session, never through a networked component.
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

    public Task<NCBankAccountData?> GetAccountAsync(NCCharacterId characterId)
    {
        return _database.GetNCBankAccountAsync(characterId);
    }

    public Task<NCBankAccountData?> AuthenticateAsync(string accountNumber, string pin)
    {
        return _database.AuthenticateNCBankAccountAsync(accountNumber, pin);
    }

    public async Task<NCBankAccountData?> TryDepositAsync(NCCharacterId characterId, int amount)
    {
        if (amount <= 0)
            return null;

        return await AdjustBalanceAsync(characterId, amount);
    }

    public async Task<NCBankAccountData?> TryWithdrawAsync(NCCharacterId characterId, int amount)
    {
        if (amount <= 0)
            return null;

        return await AdjustBalanceAsync(characterId, -amount);
    }

    private async Task<NCBankAccountData?> EnsureAccountAsync(NCCharacterId characterId)
    {
        var configuration = Configuration;
        return await _database.GetOrCreateNCBankAccountAsync(
            characterId,
            configuration.StartingBalance,
            configuration.AccountPrefix.ToUpperInvariant(),
            configuration.PinDigits);
    }

    private async Task<NCBankAccountData?> AdjustBalanceAsync(NCCharacterId characterId, int delta)
    {
        await _transactionGate.WaitAsync();
        try
        {
            return await _database.TryAdjustNCBankBalanceAsync(characterId, delta);
        }
        finally
        {
            _transactionGate.Release();
        }
    }
}
