// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Migration;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;

namespace Content.Server._NC.LivingCity.Migration;

/// <summary>
/// Versioned provenance registry for restart-safe backfill. Bindings are advanced only after their target domain state
/// is durable, so a crash safely replays the same deterministic target command before advancing this aggregate.
/// </summary>
public sealed partial class NCLegacyMigrationBindingSystem : EntitySystem
{
    public const string AggregateType = "living-city.legacy-migration-binding";
    public const int SnapshotFormatVersion = 1;

    private const int MaximumSourceKeyLength = 96;
    private const int MaximumAuditSourceLength = 128;
    private const int MutationGateStripeCount = 64;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private ITaskManager _taskManager = default!;

    private readonly ConcurrentDictionary<Guid, NCLegacyMigrationBindingRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = CreateGates();

    public int CachedBindingCount => _cache.Count;

    public async Task<NCLegacyMigrationBindingRecord?> GetAsync(Guid bindingId, bool forceReload = false)
    {
        if (bindingId == Guid.Empty)
            return null;
        if (!forceReload && _cache.TryGetValue(bindingId, out var cached))
            return cached;

        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, BindingKey(bindingId));
        var record = Deserialize(stored, bindingId);
        return record == null ? null : CacheNewest(record);
    }

    public bool Unload(Guid bindingId)
    {
        return _cache.TryRemove(bindingId, out _);
    }

    public async Task<NCLegacyBindingMutationResult> AdvanceAsync(NCAdvanceLegacyMigrationBindingRequest request)
    {
        if (!TryValidateRequest(request, out var error) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return Reject(_clock.IsReady ? NCLegacyBindingMutationStatus.Rejected :
                NCLegacyBindingMutationStatus.ClockUnavailable,
                error ?? "The migration binding or active-world clock is invalid.");
        }

        var gate = GetGate(request.Snapshot.BindingId);
        await gate.WaitAsync();
        try
        {
            var eventId = Guid.NewGuid();
            var snapshot = request.Snapshot with
            {
                ChangedAt = now,
                DataVersion = checked(request.Snapshot.DataVersion + 1),
                LastAppliedEventId = eventId,
            };
            var receipt = new BindingReceipt(snapshot, request.ExpectedVersion + 1, epoch, now);
            var persisted = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
                request.CommandId,
                "living-city.legacy-binding.advance",
                "legacy-binding",
                BindingKey(snapshot.BindingId),
                eventId,
                "living-city.legacy-binding.advanced",
                AggregateType,
                BindingKey(snapshot.BindingId),
                request.ExpectedVersion,
                SnapshotFormatVersion,
                JsonSerializer.Serialize(snapshot, JsonOptions),
                JsonSerializer.Serialize(new BindingEvent(
                    snapshot.BindingId,
                    snapshot.SourceKind,
                    snapshot.SourceKey,
                    snapshot.SourceRevision,
                    request.AuditSource), JsonOptions),
                JsonSerializer.Serialize(receipt, JsonOptions),
                request.CorrelationId,
                request.CausationId,
                epoch,
                now,
                []));

            if (persisted.Status == NCPersistentMutationStatus.Applied)
            {
                var record = CacheNewest(new NCLegacyMigrationBindingRecord(
                    snapshot,
                    persisted.AggregateVersion,
                    epoch,
                    now));
                Publish(record);
                return new(NCLegacyBindingMutationStatus.Applied, record, null);
            }

            if (persisted.Status == NCPersistentMutationStatus.DuplicateCompleted)
            {
                var replay = DeserializeReceipt(persisted.ResultPayload);
                if (replay == null || replay.Snapshot.BindingId != snapshot.BindingId)
                    return Reject(NCLegacyBindingMutationStatus.StorageFailure, "The binding receipt is invalid.");
                var record = CacheNewest(new NCLegacyMigrationBindingRecord(
                    replay.Snapshot,
                    replay.AggregateVersion,
                    replay.SessionEpoch,
                    replay.PersistedAt));
                Publish(record);
                return new(NCLegacyBindingMutationStatus.Duplicate, record, null);
            }

            return Reject(persisted.Status is NCPersistentMutationStatus.VersionConflict or
                    NCPersistentMutationStatus.DuplicateFailed
                    ? NCLegacyBindingMutationStatus.VersionConflict
                    : NCLegacyBindingMutationStatus.StorageFailure,
                persisted.Error ?? "The migration binding was not committed.");
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to advance legacy migration binding {request.Snapshot.BindingId:D}: {exception}");
            return Reject(NCLegacyBindingMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private void Publish(NCLegacyMigrationBindingRecord record)
    {
        _taskManager.RunOnMainThread(() => RaiseLocalEvent(new NCLegacyMigrationBindingPersistedEvent(
            record.Snapshot.BindingId,
            record.Snapshot.SourceKind,
            record.Snapshot.SourceKey,
            record.AggregateVersion)));
    }

    private static bool TryValidateRequest(NCAdvanceLegacyMigrationBindingRequest request, out string? error)
    {
        if (request.CommandId == Guid.Empty || request.CorrelationId == Guid.Empty || request.ExpectedVersion < 0 ||
            string.IsNullOrWhiteSpace(request.AuditSource) || request.AuditSource.Length > MaximumAuditSourceLength ||
            !Valid(request.Snapshot, request.Snapshot.BindingId))
        {
            error = "The migration binding command is invalid.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool Valid(NCLegacyMigrationBindingSnapshot? snapshot, Guid bindingId)
    {
        if (snapshot == null || bindingId == Guid.Empty || snapshot.BindingId != bindingId ||
            !Enum.IsDefined(snapshot.SourceKind) || string.IsNullOrWhiteSpace(snapshot.SourceKey) ||
            snapshot.SourceKey.Length > MaximumSourceKeyLength || snapshot.SourceKey.Trim() != snapshot.SourceKey ||
            string.IsNullOrWhiteSpace(snapshot.SourceRevision) || snapshot.SourceRevision.Length != 64 ||
            snapshot.SourceRevision.Any(value => !Uri.IsHexDigit(value)) || !snapshot.PrimaryParty.IsValid ||
            string.IsNullOrWhiteSpace(snapshot.ProjectionHash) || snapshot.ProjectionHash.Length != 64 ||
            snapshot.ProjectionHash.Any(value => !Uri.IsHexDigit(value)) || snapshot.DataVersion < 0 ||
            bindingId != NCLegacyBankJobsIdentity.Binding(
                snapshot.SourceKind.ToString(), snapshot.SourceKey))
        {
            return false;
        }

        return snapshot.SourceKind switch
        {
            NCLegacySourceKind.CharacterBank => snapshot.PrimaryParty.Kind == NCPartyKind.Character &&
                !snapshot.RelatedParty.IsValid && snapshot.AccountId.IsValid && !snapshot.EmploymentId.IsValid &&
                snapshot.ProjectedBalance >= 0,
            NCLegacySourceKind.OrganizationBank => snapshot.PrimaryParty.Kind == NCPartyKind.Organization &&
                !snapshot.RelatedParty.IsValid && snapshot.AccountId.IsValid && !snapshot.EmploymentId.IsValid &&
                snapshot.ProjectedBalance >= 0,
            NCLegacySourceKind.CharacterEmployment => snapshot.PrimaryParty.Kind == NCPartyKind.Character &&
                snapshot.RelatedParty.Kind == NCPartyKind.Organization && snapshot.AccountId.IsValid &&
                snapshot.EmploymentId.IsValid && snapshot.ProjectedBalance == 0,
            _ => false,
        };
    }

    private static NCLegacyMigrationBindingRecord? Deserialize(NCAggregateSnapshotData? stored, Guid bindingId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion || !stored.SessionEpoch.IsValid ||
            !stored.ActiveWorldTime.IsValid)
        {
            return null;
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<NCLegacyMigrationBindingSnapshot>(stored.Payload, JsonOptions);
            return Valid(snapshot, bindingId) && snapshot!.ChangedAt.IsValid && snapshot.DataVersion >= 1 &&
                   snapshot.LastAppliedEventId != Guid.Empty
                ? new NCLegacyMigrationBindingRecord(
                    snapshot!,
                    stored.AggregateVersion,
                    stored.SessionEpoch,
                    stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BindingReceipt? DeserializeReceipt(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<BindingReceipt>(payload, JsonOptions);
            return receipt != null && receipt.AggregateVersion >= 1 && receipt.SessionEpoch.IsValid &&
                   receipt.PersistedAt.IsValid && Valid(receipt.Snapshot, receipt.Snapshot.BindingId) &&
                   receipt.Snapshot.ChangedAt.IsValid && receipt.Snapshot.DataVersion >= 1 &&
                   receipt.Snapshot.LastAppliedEventId != Guid.Empty
                ? receipt
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private NCLegacyMigrationBindingRecord CacheNewest(NCLegacyMigrationBindingRecord candidate)
    {
        return _cache.AddOrUpdate(candidate.Snapshot.BindingId, candidate,
            (_, current) => current.AggregateVersion > candidate.AggregateVersion ? current : candidate);
    }

    private SemaphoreSlim GetGate(Guid bindingId)
    {
        return _gates[(bindingId.GetHashCode() & int.MaxValue) % _gates.Length];
    }

    private static SemaphoreSlim[] CreateGates()
    {
        var gates = new SemaphoreSlim[MutationGateStripeCount];
        for (var i = 0; i < gates.Length; i++)
            gates[i] = new SemaphoreSlim(1, 1);
        return gates;
    }

    private static string BindingKey(Guid bindingId) => bindingId.ToString("D");

    private static NCLegacyBindingMutationResult Reject(NCLegacyBindingMutationStatus status, string error)
    {
        return new(status, null, error);
    }

    private sealed record BindingEvent(
        Guid BindingId,
        NCLegacySourceKind SourceKind,
        string SourceKey,
        string SourceRevision,
        string AuditSource);

    private sealed record BindingReceipt(
        NCLegacyMigrationBindingSnapshot Snapshot,
        long AggregateVersion,
        NCSessionEpoch SessionEpoch,
        NCActiveWorldTime PersistedAt);
}
