// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Security.Cryptography;
using System.Text;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Persistence.RoundSnapshot;

/// <summary>
/// Immutable section captured from one domain at the common end-of-round boundary.
/// </summary>
public sealed record NCRoundSnapshotSectionCapture(
    string SectionType,
    string OwnerType,
    string OwnerId,
    int ChunkIndex,
    int FormatVersion,
    int EntityCount,
    string Payload,
    string Checksum);

/// <summary>
/// Synchronous collection event. Providers must read ECS state here and add immutable payloads only;
/// the coordinator owns the single database write performed after all handlers return.
/// </summary>
public sealed class NCRoundSnapshotCaptureEvent(
    Guid snapshotId,
    NCSessionEpoch epoch,
    NCActiveWorldTime activeWorldTime) : EntityEventArgs
{
    private readonly List<NCRoundSnapshotSectionCapture> _sections = [];
    private readonly List<string> _errors = [];
    private readonly HashSet<(string SectionType, string OwnerType, string OwnerId, int ChunkIndex)> _keys = [];
    private readonly HashSet<int> _deletedCharacterIds = [];

    public Guid SnapshotId { get; } = snapshotId;
    public NCSessionEpoch Epoch { get; } = epoch;
    public NCActiveWorldTime ActiveWorldTime { get; } = activeWorldTime;
    public IReadOnlyList<NCRoundSnapshotSectionCapture> Sections => _sections;
    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlySet<int> DeletedCharacterIds => _deletedCharacterIds;

    /// <summary>
    /// Marks an irreversibly dead profile for deletion in the same transaction that publishes this snapshot.
    /// </summary>
    public void DeleteCharacter(int characterId)
    {
        if (characterId <= 0)
        {
            Fail($"Invalid deleted character ID {characterId}.");
            return;
        }

        _deletedCharacterIds.Add(characterId);
    }

    public bool TryAddSection(
        string sectionType,
        string ownerType,
        string ownerId,
        int formatVersion,
        int entityCount,
        string payload,
        int chunkIndex = 0)
    {
        if (string.IsNullOrWhiteSpace(sectionType) || sectionType.Length > 64 ||
            string.IsNullOrWhiteSpace(ownerType) || ownerType.Length > 64 ||
            string.IsNullOrWhiteSpace(ownerId) || ownerId.Length > 128 ||
            formatVersion <= 0 || entityCount < 0 || chunkIndex < 0 ||
            !_keys.Add((sectionType, ownerType, ownerId, chunkIndex)))
        {
            Fail($"Invalid or duplicate snapshot section {sectionType}/{ownerType}/{ownerId}/{chunkIndex}.");
            return false;
        }

        _sections.Add(new NCRoundSnapshotSectionCapture(
            sectionType,
            ownerType,
            ownerId,
            chunkIndex,
            formatVersion,
            entityCount,
            payload,
            Hash(payload)));
        return true;
    }

    public void Fail(string error)
    {
        if (!string.IsNullOrWhiteSpace(error))
            _errors.Add(error);
    }

    public static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>
/// Raised on the main thread after the snapshot and its character deletions are committed.
/// Consumers may register cache refreshes that must finish before round teardown continues.
/// </summary>
public sealed class NCRoundSnapshotCommittedEvent(
    Guid snapshotId,
    IReadOnlySet<int> deletedCharacterIds) : EntityEventArgs
{
    private readonly List<Task> _pending = [];

    public Guid SnapshotId { get; } = snapshotId;
    public IReadOnlySet<int> DeletedCharacterIds { get; } = deletedCharacterIds;
    public IReadOnlyList<Task> Pending => _pending;

    public void AddPending(Task task) => _pending.Add(task);
}

public enum NCRoundSnapshotCoordinatorState : byte
{
    Idle,
    Capturing,
    Writing,
    Committed,
    Failed,
}
