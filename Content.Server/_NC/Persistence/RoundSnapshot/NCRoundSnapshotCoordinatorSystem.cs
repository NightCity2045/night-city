// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json;
using Content.Server.Database;
using Content.Server.GameTicking;
using Content.Server.Maps;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;

namespace Content.Server._NC.Persistence.RoundSnapshot;

/// <summary>
/// Owns the only full-state database commit at the end of an active-world epoch.
/// Domain systems contribute immutable sections through <see cref="NCRoundSnapshotCaptureEvent"/>.
/// </summary>
public sealed partial class NCRoundSnapshotCoordinatorSystem : EntitySystem
{
    private const int SnapshotSchemaVersion = 1;
    private const int RetainedSnapshots = 3;
    private const string MetadataSection = "system-metadata";
    private const string WorldOwnerType = "world";

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private IGameMapManager _maps = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private ITaskManager _taskManager = default!;

    private NCSessionEpoch _capturedEpoch;
    private Task _pendingCommit = Task.CompletedTask;

    public NCRoundSnapshotCoordinatorState State { get; private set; }
    public Guid LastSnapshotId { get; private set; }
    public string? LastError { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        _capturedEpoch = default;
        _pendingCommit = Task.CompletedTask;
        State = NCRoundSnapshotCoordinatorState.Idle;
        LastError = null;
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        if (_capturedEpoch == args.Epoch)
        {
            args.AddPendingWrite(_pendingCommit);
            return;
        }

        _capturedEpoch = args.Epoch;
        LastSnapshotId = Guid.NewGuid();
        State = NCRoundSnapshotCoordinatorState.Capturing;
        LastError = null;

        var capture = new NCRoundSnapshotCaptureEvent(LastSnapshotId, args.Epoch, args.Time);
        var map = _maps.GetSelectedMap();
        var mapId = map?.ID ?? string.Empty;
        var metadata = JsonSerializer.Serialize(new SnapshotMetadata(
            SnapshotSchemaVersion,
            _ticker.RoundId,
            mapId,
            args.Epoch.Value,
            args.Time.Ticks));
        capture.TryAddSection(
            MetadataSection,
            WorldOwnerType,
            args.Epoch.Value.ToString("D"),
            SnapshotSchemaVersion,
            0,
            metadata);

        RaiseLocalEvent(capture);
        if (capture.Errors.Count != 0)
        {
            State = NCRoundSnapshotCoordinatorState.Failed;
            LastError = string.Join(" ", capture.Errors);
            _pendingCommit = Task.FromException(new InvalidOperationException(LastError));
            args.AddPendingWrite(_pendingCommit);
            return;
        }

        State = NCRoundSnapshotCoordinatorState.Writing;
        var sections = capture.Sections
            .Select(value => new NCRoundSnapshotSectionWrite(
                value.SectionType,
                value.OwnerType,
                value.OwnerId,
                value.ChunkIndex,
                value.FormatVersion,
                value.EntityCount,
                value.Payload,
                value.Checksum))
            .ToArray();
        var checksum = ComputeSnapshotChecksum(sections);
        var request = new NCRoundSnapshotCommitRequest(
            LastSnapshotId,
            args.Epoch.Value,
            args.Time.Ticks,
            SnapshotSchemaVersion,
            mapId,
            0,
            string.Empty,
            checksum,
            DateTime.UtcNow,
            sections,
            capture.DeletedCharacterIds.ToArray(),
            RetainedSnapshots);
        _pendingCommit = CommitAsync(request);
        args.AddPendingWrite(_pendingCommit);
    }

    private async Task CommitAsync(NCRoundSnapshotCommitRequest request)
    {
        try
        {
            var result = await _database.CommitNCRoundSnapshotAsync(request);
            if (result.Status is not (NCRoundSnapshotCommitStatus.Committed or
                NCRoundSnapshotCommitStatus.Duplicate))
            {
                throw new InvalidOperationException(result.Error ?? $"Snapshot commit returned {result.Status}.");
            }

            State = NCRoundSnapshotCoordinatorState.Committed;
            await RaiseCommittedOnMainThreadAsync(request.SnapshotId, request.DeletedCharacterIds);
        }
        catch (Exception exception)
        {
            State = NCRoundSnapshotCoordinatorState.Failed;
            LastError = exception.Message;
            Log.Error($"Round snapshot {request.SnapshotId} failed: {exception}");
            throw;
        }
    }

    private Task RaiseCommittedOnMainThreadAsync(Guid snapshotId, IReadOnlyList<int> deletedCharacterIds)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _taskManager.RunOnMainThread(async () =>
        {
            try
            {
                var ev = new NCRoundSnapshotCommittedEvent(snapshotId, deletedCharacterIds.ToHashSet());
                RaiseLocalEvent(ev);
                await Task.WhenAll(ev.Pending);
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return completion.Task;
    }

    private static string ComputeSnapshotChecksum(IEnumerable<NCRoundSnapshotSectionWrite> sections)
    {
        var canonical = string.Join('\n', sections
            .OrderBy(value => value.SectionType, StringComparer.Ordinal)
            .ThenBy(value => value.OwnerType, StringComparer.Ordinal)
            .ThenBy(value => value.OwnerId, StringComparer.Ordinal)
            .ThenBy(value => value.ChunkIndex)
            .Select(value => $"{value.SectionType}\u001f{value.OwnerType}\u001f{value.OwnerId}\u001f{value.ChunkIndex}\u001f{value.FormatVersion}\u001f{value.EntityCount}\u001f{value.Checksum}"));
        return NCRoundSnapshotCaptureEvent.Hash(canonical);
    }

    private sealed record SnapshotMetadata(
        int SchemaVersion,
        int RoundId,
        string MapId,
        Guid SessionEpoch,
        long ActiveWorldTicks);
}
