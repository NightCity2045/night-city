// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server.Database;

namespace Content.Server._NC.Meta.Persistence;

public sealed partial class NCMetaPersistenceSystem
{
    /// <summary>
    /// Authenticates a historical acknowledgement without re-executing its effects or requiring
    /// its installation to remain active. Every META participant must still have an authenticated
    /// selected-checkpoint/journal state at or beyond the acknowledged version.
    /// </summary>
    public async Task<bool> CanReplayCommandAsync(NCPersistentCommandResultData receipt, Guid requestId,
        string commandType, string owner, string fingerprint, string expectedScopeType = "meta.owner", string? expectedScopeId = null)
    {
        // Non-owner scopes (for example a paid ledger transaction) are computed by the
        // trusted native caller from its actor/request. They are never copied from a receipt.
        if (owner.Length == 0 || expectedScopeType is not { Length: > 0 and <= 64 } ||
            expectedScopeType == "meta.owner" && expectedScopeId != null && expectedScopeId != owner ||
            expectedScopeType != "meta.owner" && expectedScopeId is not { Length: > 0 and <= 128 }) return false;
        var scopeId = expectedScopeId ?? owner;
        if (requestId == Guid.Empty || receipt.CommandId != requestId || receipt.CommandType != commandType ||
            receipt.ScopeType != expectedScopeType || receipt.ScopeId != scopeId || receipt.RequestFingerprint != fingerprint ||
            receipt.Status != NCPersistentMutationStatus.DuplicateCompleted ||
            receipt.Aggregates is not { Count: >= 1 and <= 256 } participants ||
            receipt.ResultPayload is not { Length: <= 2 * MaximumProjectBytes }) return false;
        var generation = _snapshots.SelectionGeneration;
        var keys = new HashSet<(string, string)>();
        var verified = 0;
        try
        {
            foreach (var participant in participants)
            {
                if (participant is not { AggregateType: { Length: > 0 and <= 64 } type,
                        AggregateId: { Length: > 0 and <= 128 } id, AggregateVersion: > 0 }) return false;
                if (!type.StartsWith("meta.", StringComparison.Ordinal)) continue;
                if (!keys.Add((type, id))) return false;
                var current = await _snapshots.ReadCurrentAsync(participant.AggregateType, participant.AggregateId);
                if (current == null || current.AggregateVersion < participant.AggregateVersion ||
                    generation != _snapshots.SelectionGeneration) return false;
                verified++;
            }
        }
        catch (NCMetaSnapshotIntegrityException) { return false; }
        return verified > 0 && generation == _snapshots.SelectionGeneration;
    }
}
