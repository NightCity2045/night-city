// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared._NC.TraumaTeam;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.TraumaTeam;

public enum NCTraumaTeamPurchaseStatus : byte
{
    Applied,
    Duplicate,
    Invalid,
    PlanLocked,
    InsufficientFunds,
    ClockUnavailable,
    StorageFailure,
}

public sealed record NCTraumaTeamSubscriptionSnapshot(
    NCCharacterId CharacterId,
    string PlanId,
    NCActiveWorldTime StartsAt,
    NCActiveWorldTime ExpiresAt,
    NCTransactionId LastTransactionId,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCTraumaTeamSubscriptionRecord(
    NCTraumaTeamSubscriptionSnapshot Snapshot,
    long AggregateVersion,
    NCSessionEpoch SessionEpoch,
    NCActiveWorldTime PersistedAt);

public sealed record NCTraumaTeamCoverage(
    string PlanId,
    int DispatchPriority,
    int TreatmentCoverageBasisPoints,
    int MaximumThreatBasisPoints,
    NCActiveWorldTime ExpiresAt);

public sealed record NCTraumaTeamPurchaseResult(
    NCTraumaTeamPurchaseStatus Status,
    NCTraumaTeamSubscriptionRecord? Subscription,
    string? Error);

/// <summary>
/// Stores one lazy-expiring coverage aggregate per character. The ledger commits money and coverage in one
/// serializable transaction, so a charged character can never be left without the purchased entitlement.
/// </summary>
public sealed partial class NCTraumaTeamSubscriptionSystem : EntitySystem
{
    public const int MinimumDays = 1;
    public const int MaximumDaysPerPurchase = 30;
    private const int MaximumCoverageDays = 365;
    private const string AggregateType = "trauma-team.subscription";
    private const int SnapshotFormatVersion = 1;
    private const string AuditSource = "agent-trauma-team";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private readonly ConcurrentDictionary<NCCharacterId, NCTraumaTeamSubscriptionRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = CreateGates();

    public async Task<NCTraumaTeamSubscriptionRecord?> GetAsync(NCCharacterId characterId, bool forceReload = false)
    {
        if (!characterId.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(characterId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, Key(characterId));
        var record = Deserialize(stored, characterId);
        if (record != null)
            _cache[characterId] = record;
        else
            _cache.TryRemove(characterId, out _);
        return record;
    }

    public async Task<NCTraumaTeamCoverage?> GetActiveCoverageAsync(NCCharacterId characterId)
    {
        if (!_clock.TryGetCurrentTime(out _, out var now) ||
            await GetAsync(characterId) is not { } record || record.Snapshot.ExpiresAt <= now ||
            !_prototypes.TryIndex<NCTraumaTeamSubscriptionPlanPrototype>(record.Snapshot.PlanId, out var plan))
            return null;

        return new(record.Snapshot.PlanId, plan.DispatchPriority, plan.TreatmentCoverageBasisPoints,
            plan.MaximumThreatBasisPoints, record.Snapshot.ExpiresAt);
    }

    public async Task<NCTraumaTeamPurchaseResult> TryPurchaseAsync(
        NCCharacterId characterId,
        string planId,
        int days,
        Guid commandId)
    {
        if (!characterId.IsValid || commandId == Guid.Empty || days is < MinimumDays or > MaximumDaysPerPurchase ||
            !_prototypes.TryIndex<NCTraumaTeamSubscriptionPlanPrototype>(planId, out var plan) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return new(_clock.IsReady ? NCTraumaTeamPurchaseStatus.Invalid : NCTraumaTeamPurchaseStatus.ClockUnavailable,
                null, null);

        var gate = _gates[(uint) characterId.Value % _gates.Length];
        await gate.WaitAsync();
        try
        {
            var current = await GetAsync(characterId, true);
            if (current != null && current.Snapshot.ExpiresAt > now && current.Snapshot.PlanId != plan.ID)
                return new(NCTraumaTeamPurchaseStatus.PlanLocked, current, null);

            var basis = current?.Snapshot.ExpiresAt > now ? current.Snapshot.ExpiresAt : now;
            var expiresAt = basis.Advance(TimeSpan.FromDays(days));
            if (expiresAt > now.Advance(TimeSpan.FromDays(MaximumCoverageDays)))
                return new(NCTraumaTeamPurchaseStatus.Invalid, current, null);

            long price;
            try
            {
                price = checked(plan.PricePerDay * days);
            }
            catch (OverflowException)
            {
                return new(NCTraumaTeamPurchaseStatus.Invalid, current, null);
            }

            var payerParty = NCPartyRef.ForCharacter(characterId);
            var payeeParty = NCPersistentIdentity.Organization("TraumaTeam");
            var payer = await _finance.EnsurePrimaryAccountAsync(payerParty, AuditSource, commandId);
            var payee = await _finance.EnsureOrganizationAsync("TraumaTeam", "Trauma Team", AuditSource);
            if (!payer.IsResolved || !payee.IsResolved)
                return new(NCTraumaTeamPurchaseStatus.StorageFailure, current, payer.Error ?? payee.Error);

            var eventId = Guid.NewGuid();
            var transactionId = new NCTransactionId(commandId);
            var snapshot = new NCTraumaTeamSubscriptionSnapshot(
                characterId,
                plan.ID,
                current == null || current.Snapshot.ExpiresAt <= now ? now : current.Snapshot.StartsAt,
                expiresAt,
                transactionId,
                checked((current?.Snapshot.DataVersion ?? 0) + 1),
                eventId);
            var participant = new NCAtomicLedgerParticipantWrite(
                eventId,
                "trauma-team.subscription.purchased",
                AggregateType,
                Key(characterId),
                current?.AggregateVersion ?? 0,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(snapshot, JsonOptions),
                JsonSerializer.Serialize(new { characterId = characterId.Value, planId = plan.ID, days, price }, JsonOptions));
            var result = await _ledger.PostTransactionAsync(new NCPostLedgerTransactionRequest(
                commandId,
                transactionId,
                NCLedgerTransactionKind.Purchase,
                $"Trauma Team {plan.ID} coverage for {days} day(s)",
                [
                    new(payer.AccountId, payer.Account!.AggregateVersion, -price),
                    new(payee.AccountId, payee.Account!.AggregateVersion, price),
                ],
                payerParty,
                commandId,
                commandId,
                AuditSource,
                [participant]));
            if (result.Status is not (NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate))
            {
                var status = result.Status == NCEconomyMutationStatus.InsufficientFunds
                    ? NCTraumaTeamPurchaseStatus.InsufficientFunds
                    : NCTraumaTeamPurchaseStatus.StorageFailure;
                return new(status, current, result.Error);
            }

            var saved = await GetAsync(characterId, true);
            return saved == null
                ? new(NCTraumaTeamPurchaseStatus.StorageFailure, null, "Coverage could not be reloaded.")
                : new(result.Status == NCEconomyMutationStatus.Applied
                    ? NCTraumaTeamPurchaseStatus.Applied
                    : NCTraumaTeamPurchaseStatus.Duplicate, saved, null);
        }
        finally
        {
            gate.Release();
        }
    }

    private static NCTraumaTeamSubscriptionRecord? Deserialize(NCAggregateSnapshotData? stored, NCCharacterId expected)
    {
        if (stored is not { FormatVersion: SnapshotFormatVersion })
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCTraumaTeamSubscriptionSnapshot>(stored.Payload, JsonOptions);
            if (snapshot == null || snapshot.CharacterId != expected || string.IsNullOrWhiteSpace(snapshot.PlanId) ||
                snapshot.StartsAt > snapshot.ExpiresAt || snapshot.DataVersion < 1 ||
                snapshot.LastAppliedEventId == Guid.Empty)
                return null;
            return new(snapshot, stored.AggregateVersion, stored.SessionEpoch, stored.ActiveWorldTime);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Key(NCCharacterId characterId) => characterId.Value.ToString();

    private static SemaphoreSlim[] CreateGates()
    {
        var gates = new SemaphoreSlim[64];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }
}
