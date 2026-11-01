// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Economy;

/// <summary>Trusted immutable input for all unique-item ownership changes, including installed parts.</summary>
public sealed record NCItemOwnershipContext(Guid OperationId, NCItemInstanceRecord Root,
    IReadOnlyList<NCItemInstanceRecord> Parts, NCPartyRef PreviousOwner, NCPartyRef NewOwner,
    NCSessionEpoch Epoch, NCActiveWorldTime Time);

/// <summary>Extensions join the existing transaction; they must never commit ownership independently.</summary>
public interface INCItemOwnershipParticipant
{
    string Name { get; }
    Task<NCItemOwnershipPreparation?> PrepareAsync(NCItemOwnershipContext context);
    /// <summary>Idempotently reload durable authority after commit, rollback, uncertain outcome, or replay.</summary>
    Task ReconcileAsync(NCItemOwnershipContext context);
}

public sealed record NCItemOwnershipPreparation(IReadOnlyList<NCPersistentAggregateWrite> Writes,
    Func<Task<bool>> TryBeginCommit, IDisposable? Drain = null);

public sealed partial class NCEconomicAssetRegistrySystem
{
    public const int MaximumItemOwnershipWrites = 32;
    private readonly object _itemParticipantsLock = new();
    private readonly List<INCItemOwnershipParticipant> _itemParticipants = [];

    public void RegisterItemOwnershipParticipant(INCItemOwnershipParticipant participant)
    {
        lock (_itemParticipantsLock)
        {
            if (_itemParticipants.Count >= 8 || string.IsNullOrWhiteSpace(participant.Name) ||
                participant.Name.Length > 64 || _itemParticipants.Any(value => value.Name == participant.Name))
                throw new InvalidOperationException("Invalid or duplicate item ownership participant, or provider limit exceeded.");
            _itemParticipants.Add(participant);
        }
    }

    public void UnregisterItemOwnershipParticipant(INCItemOwnershipParticipant participant)
    {
        lock (_itemParticipantsLock) _itemParticipants.Remove(participant);
    }

    private INCItemOwnershipParticipant[] OwnershipParticipants()
    {
        lock (_itemParticipantsLock) return _itemParticipants.ToArray();
    }

    private async Task<NCItemOwnershipContext?> LoadOwnershipContextAsync(Guid operationId, string itemId,
        NCPartyRef previousOwner, NCPartyRef newOwner, NCSessionEpoch epoch, NCActiveWorldTime time)
    {
        if (!Guid.TryParseExact(itemId, "D", out var id)) return null;
        var root = await GetItemAsync(new NCItemId(id), true);
        var parts = root == null ? null : await LoadAssemblyPartsAsync(root);
        return root == null || parts == null ? null : new(operationId, root, parts, previousOwner, newOwner, epoch, time);
    }

    private async Task<ItemOwnershipScope> PrepareOwnershipAsync(NCItemOwnershipContext? context,
        List<NCPersistentAggregateWrite> writes, int reservedWrites = 0, int maximumParticipants = MaximumItemOwnershipWrites)
    {
        var scope = new ItemOwnershipScope(this, context);
        try
        {
            if (context == null) throw new InvalidOperationException("The item ownership context could not be loaded.");
            foreach (var provider in OwnershipParticipants())
            {
                var prepared = await provider.PrepareAsync(context);
                if (prepared == null) throw new InvalidOperationException($"Item ownership participant '{provider.Name}' rejected the transfer.");
                scope.Prepared.Add((provider, prepared));
                if (prepared.Writes.Count > MaximumItemOwnershipWrites * 2)
                    throw new InvalidOperationException("Item ownership participant exceeds its bounded write budget.");
                writes.AddRange(prepared.Writes);
            }
            // Identical CAS snapshots may be supplied by two participants. Conflicting snapshots never merge.
            var unique = new Dictionary<(string Type, string Id), NCPersistentAggregateWrite>();
            foreach (var write in writes)
            {
                var key = (write.AggregateType, write.AggregateId);
                if (unique.TryGetValue(key, out var existing))
                {
                    if (existing.ExpectedAggregateVersion != write.ExpectedAggregateVersion ||
                        existing.SnapshotFormatVersion != write.SnapshotFormatVersion ||
                        existing.SnapshotPayload != write.SnapshotPayload || existing.EventType != write.EventType ||
                        existing.EventPayload != write.EventPayload)
                        throw new InvalidOperationException("Conflicting item ownership participant writes.");
                    continue;
                }
                unique.Add(key, write);
                if (unique.Count + reservedWrites > MaximumItemOwnershipWrites || unique.Count > maximumParticipants)
                    throw new InvalidOperationException("Item ownership transaction exceeds the 32 aggregate write budget or ledger participant limit.");
            }
            writes.Clear();
            writes.AddRange(unique.Values);
        }
        catch (Exception exception) { scope.Error = exception.Message; }
        return scope;
    }

    private async Task ReconcileOwnershipAsync(NCItemOwnershipContext? context)
    {
        if (context == null) throw new InvalidOperationException("The committed item ownership context could not be reloaded.");
        foreach (var provider in OwnershipParticipants()) await provider.ReconcileAsync(context);
    }

    private sealed class ItemOwnershipScope(NCEconomicAssetRegistrySystem owner, NCItemOwnershipContext? context) : IAsyncDisposable
    {
        public readonly List<(INCItemOwnershipParticipant Provider, NCItemOwnershipPreparation Preparation)> Prepared = [];
        public string? Error;
        private bool _enteredCommit;

        public async Task<bool> TryBeginCommitAsync()
        {
            if (Error != null) return false;
            if (context == null || !owner._clock.TryGetCurrentTime(out var epoch, out _) || epoch != context.Epoch)
            { Error = "The active-world epoch changed before item ownership commit."; return false; }
            _enteredCommit = true;
            foreach (var (_, preparation) in Prepared)
                if (!await preparation.TryBeginCommit()) { Error = "Item ownership authority changed before commit."; return false; }
            return true;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_enteredCommit && context != null)
                    foreach (var (provider, _) in Prepared)
                        try { await provider.ReconcileAsync(context); }
                        catch (Exception exception) { owner.Log.Error($"Item ownership reconciliation '{provider.Name}' failed; native authority must remain suspended: {exception}"); }
            }
            finally { foreach (var (_, preparation) in Prepared) preparation.Drain?.Dispose(); }
        }
    }
}
