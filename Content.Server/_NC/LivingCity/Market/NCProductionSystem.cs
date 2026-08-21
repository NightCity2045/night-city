// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Concurrent;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Market;

/// <summary>
/// Active-world production coordinator. It is the sole cross-aggregate boundary that reserves/consumes recipe inputs,
/// creates provenance-bearing outputs, and advances the business queue in one serializable database transaction.
/// </summary>
public sealed partial class NCProductionSystem : EntitySystem
{
    public const string AggregateType = "living-city.production-job";
    private const int SnapshotFormatVersion = 1;
    private const int RecoveryPageSize = 100;
    private const int MaximumRecoveredJobs = 10_000;
    private const int MaximumCompletionsPerFrame = 8;
    private const int MaximumCombinedRecipeEntries = 20;
    private static readonly ProtoId<NCMarketPolicyPrototype> DefaultPolicy = "Default";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCBusinessRegistrySystem _businesses = default!;
    [Dependency] private NCEconomicAssetRegistrySystem _assets = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    private readonly ConcurrentDictionary<NCProductionJobId, NCProductionJobRecord> _cache = [];
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly PriorityQueue<NCProductionJobId, long> _due = new();
    private readonly HashSet<NCProductionJobId> _queued = [];
    private readonly HashSet<NCProductionJobId> _processing = [];
    private readonly HashSet<Task> _inFlight = [];
    private bool _active;
    private int _generation;

    public int CachedJobCount => _cache.Count;
    public int ScheduledJobCount => _queued.Count;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
        SubscribeLocalEvent<NCActiveWorldSessionClosedEvent>(OnSessionClosed);
        SubscribeLocalEvent<NCActiveWorldClockFailedEvent>(OnClockFailed);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_active || !_clock.TryGetCurrentTime(out _, out var now))
            return;
        var processed = 0;
        while (processed < MaximumCompletionsPerFrame && _due.TryPeek(out var jobId, out var dueTicks) &&
               dueTicks <= now.Ticks)
        {
            _due.Dequeue();
            _queued.Remove(jobId);
            if (_processing.Add(jobId))
                Track(CompleteScheduledAsync(jobId, _generation));
            processed++;
        }
    }

    public async Task<NCProductionJobRecord?> GetAsync(NCProductionJobId id, bool forceReload = false)
    {
        if (!id.IsValid)
            return null;
        if (!forceReload && _cache.TryGetValue(id, out var cached))
            return cached;
        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, id.ToString());
        var record = Deserialize(stored, id);
        return record == null ? null : _cache.AddOrUpdate(id, record,
            (_, current) => current.AggregateVersion > record.AggregateVersion ? current : record);
    }

    public async Task<NCProductionMutationResult> StartAsync(NCStartProductionRequest request)
    {
        if (!Valid(request) || !TryPolicy(out var policy) ||
            !_prototypes.TryIndex(request.Recipe, out var recipe) || !Valid(recipe, policy) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(_clock.IsReady ? NCMarketMutationStatus.Rejected : NCMarketMutationStatus.ClockUnavailable,
                "The production request, recipe, policy, or active-world clock is invalid.");

        var gate = Gate(request.JobId.Value);
        await gate.WaitAsync();
        try
        {
            var existing = await GetAsync(request.JobId, true);
            if (existing != null)
                return existing.Snapshot.BusinessId == request.BusinessId &&
                       existing.Snapshot.RecipeId == recipe.ID && existing.Snapshot.Runs == request.Runs
                    ? new(NCMarketMutationStatus.Duplicate, existing,
                        await _businesses.GetAsync(request.BusinessId, true), null)
                    : Reject(NCMarketMutationStatus.Rejected,
                        "The production job identity belongs to different immutable data.");
            var business = await _businesses.GetAsync(request.BusinessId, true);
            if (business == null)
                return Reject(NCMarketMutationStatus.NotFound, "The production business does not exist.");
            if (business.AggregateVersion != request.ExpectedBusinessVersion)
                return Reject(NCMarketMutationStatus.VersionConflict, "The business version changed.");
            if (business.Snapshot.Status is not (NCBusinessStatus.Active or NCBusinessStatus.Shortage) ||
                !recipe.AllowedBusinessKinds.Contains(business.Snapshot.Kind) ||
                request.Initiator != business.Snapshot.Owner &&
                !business.Snapshot.Operators.Contains(request.Initiator) ||
                business.Snapshot.ProductionQueue.Count >= business.Snapshot.Policy.MaximumConcurrentProduction)
                return Reject(NCMarketMutationStatus.Rejected,
                    "The business cannot run this recipe or its production capacity is exhausted.");
            var requestedOutputEntries = checked(recipe.CommodityOutputs.Count +
                recipe.ItemOutputs.Sum(value => checked(value.Quantity * request.Runs)));
            if (requestedOutputEntries > MaximumCombinedRecipeEntries)
                return Reject(NCMarketMutationStatus.Rejected,
                    "The requested production run exceeds the bounded atomic output budget.");

            var inputNode = await _assets.GetStockNodeAsync(business.Snapshot.InventoryStockNodeId, true);
            if (inputNode == null || inputNode.Snapshot.Owner != business.Snapshot.BusinessParty)
                return Reject(NCMarketMutationStatus.StorageFailure,
                    "The business input stock authority is missing.");
            var allocations = await SelectInputsAsync(inputNode, recipe, request.Runs, now);
            if (allocations == null)
                return Reject(NCMarketMutationStatus.Rejected,
                    "The business does not have enough unreserved, unexpired recipe inputs.");

            var duration = TimeSpan.FromTicks(checked(recipe.Duration.Ticks * request.Runs));
            var dueAt = now.Advance(duration);
            var jobEventId = Guid.NewGuid();
            var job = new NCProductionJobSnapshot(
                request.JobId,
                request.BusinessId,
                recipe.ID,
                request.Runs,
                allocations.Select(value => new NCProductionInputAllocation(
                    value.Batch.Snapshot.BatchId,
                    inputNode.Snapshot.StockNodeId,
                    value.Quantity,
                    value.Batch.AggregateVersion + 1,
                    inputNode.AggregateVersion + 1)).ToArray(),
                business.Snapshot.OutputStockNodeId,
                NCProductionJobStatus.InProgress,
                now,
                dueAt,
                string.Empty,
                now,
                now,
                1,
                jobEventId);
            var writes = new List<NCPersistentAggregateWrite>(allocations.Count + 3);
            foreach (var allocation in allocations)
            {
                var eventId = Guid.NewGuid();
                var next = allocation.Batch.Snapshot with
                {
                    ReservedQuantity = checked(allocation.Batch.Snapshot.ReservedQuantity + allocation.Quantity),
                    UpdatedAt = now,
                    DataVersion = checked(allocation.Batch.Snapshot.DataVersion + 1),
                    LastAppliedEventId = eventId,
                };
                writes.Add(Write(allocation.Batch, next, eventId,
                    "living-city.commodity-batch.production-reserved", request.AuditSource));
            }
            var nodeEventId = Guid.NewGuid();
            var nextNode = inputNode.Snapshot with
            {
                UpdatedAt = now,
                DataVersion = checked(inputNode.Snapshot.DataVersion + 1),
                LastAppliedEventId = nodeEventId,
            };
            writes.Add(Write(inputNode, nextNode, nodeEventId,
                "living-city.stock-node.production-reserved", request.AuditSource));
            var businessEventId = Guid.NewGuid();
            var nextBusiness = business.Snapshot with
            {
                ProductionQueue = business.Snapshot.ProductionQueue.Append(request.JobId).ToArray(),
                Status = NCBusinessStatus.Active,
                UpdatedAt = now,
                DataVersion = checked(business.Snapshot.DataVersion + 1),
                LastAppliedEventId = businessEventId,
            };
            writes.Add(Write(business, nextBusiness, businessEventId,
                "living-city.business.production-started", request.AuditSource));
            writes.Add(Write(null, job, jobEventId,
                "living-city.production-job.started", request.AuditSource));

            var persisted = await _database.ApplyNCPersistentBatchMutationAsync(new NCPersistentBatchMutationRequest(
                request.CommandId,
                "living-city.production-job.start",
                "production-job",
                request.JobId.ToString(),
                writes,
                JsonSerializer.Serialize(new Receipt(request.JobId, 1), JsonOptions),
                request.CorrelationId,
                request.CausationId,
                epoch,
                now,
                []));
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return Reject(Map(persisted.Status), persisted.Error ?? "The production job was not committed.");
            await RefreshParticipantsAsync(job, business.Snapshot, allocations.Select(value => value.Batch));
            var loaded = await GetAsync(request.JobId, true);
            var loadedBusiness = await _businesses.GetAsync(request.BusinessId, true);
            if (loaded == null || loadedBusiness == null)
                return Reject(NCMarketMutationStatus.StorageFailure,
                    "The committed production job could not be reloaded.");
            Enqueue(loaded.Snapshot.JobId, loaded.Snapshot.DueAt);
            return new(persisted.Status == NCPersistentMutationStatus.Applied
                ? NCMarketMutationStatus.Applied
                : NCMarketMutationStatus.Duplicate, loaded, loadedBusiness, null);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to start production job {request.JobId}: {exception}");
            return Reject(NCMarketMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<NCProductionMutationResult> CancelAsync(NCCancelProductionRequest request)
    {
        if (request.CommandId == Guid.Empty || !request.JobId.IsValid || request.ExpectedJobVersion <= 0 ||
            !request.Initiator.IsValid || request.CorrelationId == Guid.Empty || !ValidAudit(request.AuditSource) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return Reject(_clock.IsReady ? NCMarketMutationStatus.Rejected : NCMarketMutationStatus.ClockUnavailable,
                "The production cancellation request is invalid.");
        return await CloseWithoutOutputsAsync(
            request.CommandId, request.JobId, request.ExpectedJobVersion, NCProductionJobStatus.Cancelled,
            "cancelled", request.Initiator, request.CorrelationId, request.CausationId,
            request.AuditSource, epoch, now);
    }

    private async Task CompleteScheduledAsync(NCProductionJobId jobId, int generation)
    {
        try
        {
            if (!_active || generation != _generation || !_clock.TryGetCurrentTime(out var epoch, out var now))
                return;
            var result = await CompleteAsync(jobId, epoch, now);
            if ((result.Status is NCMarketMutationStatus.VersionConflict or NCMarketMutationStatus.StorageFailure) &&
                _active && generation == _generation)
                Enqueue(jobId, now.Advance(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            _processing.Remove(jobId);
        }
    }

    private async Task<NCProductionMutationResult> CompleteAsync(
        NCProductionJobId jobId,
        NCSessionEpoch epoch,
        NCActiveWorldTime now)
    {
        var gate = Gate(jobId.Value);
        await gate.WaitAsync();
        try
        {
            var job = await GetAsync(jobId, true);
            if (job == null)
                return Reject(NCMarketMutationStatus.NotFound, "The scheduled production job does not exist.");
            if (job.Snapshot.Status == NCProductionJobStatus.Completed)
                return new(NCMarketMutationStatus.Duplicate, job,
                    await _businesses.GetAsync(job.Snapshot.BusinessId, true), null);
            if (job.Snapshot.Status != NCProductionJobStatus.InProgress || now < job.Snapshot.DueAt)
                return Reject(NCMarketMutationStatus.Rejected, "The production job is not due for completion.");
            var business = await _businesses.GetAsync(job.Snapshot.BusinessId, true);
            if (business == null || !_prototypes.TryIndex(
                    new ProtoId<NCProductionRecipePrototype>(job.Snapshot.RecipeId), out var recipe) ||
                !TryPolicy(out var policy) || !Valid(recipe, policy))
            {
                return Reject(NCMarketMutationStatus.StorageFailure,
                    "The production business, recipe, or policy is unavailable.");
            }
            var inputNode = await _assets.GetStockNodeAsync(business.Snapshot.InventoryStockNodeId, true);
            var outputNode = await _assets.GetStockNodeAsync(job.Snapshot.OutputStockNodeId, true);
            if (inputNode == null || outputNode == null)
                return Reject(NCMarketMutationStatus.StorageFailure, "A production stock node is unavailable.");
            var inputs = new List<(NCProductionInputAllocation Allocation, NCCommodityBatchRecord Batch)>();
            foreach (var allocation in job.Snapshot.Inputs)
            {
                var batch = await _assets.GetCommodityBatchAsync(allocation.BatchId, true);
                if (batch == null || batch.Snapshot.Owner != business.Snapshot.BusinessParty ||
                    batch.Snapshot.Location != StockLocation(inputNode.Snapshot.StockNodeId) ||
                    batch.Snapshot.Quantity < allocation.Quantity ||
                    batch.Snapshot.ReservedQuantity < allocation.Quantity)
                {
                    return Reject(NCMarketMutationStatus.VersionConflict,
                        "A production input reservation is inconsistent.");
                }
                inputs.Add((allocation, batch));
            }

            var outputCount = checked(recipe.CommodityOutputs.Count +
                recipe.ItemOutputs.Sum(value => checked(value.Quantity * job.Snapshot.Runs)));
            if (outputNode.Snapshot.CommodityBatchIds.Count + recipe.CommodityOutputs.Count > 512 ||
                outputNode.Snapshot.ItemIds.Count + recipe.ItemOutputs.Sum(value => value.Quantity) > 512 ||
                inputs.Count + outputCount > MaximumCombinedRecipeEntries)
                return Reject(NCMarketMutationStatus.CapacityExceeded,
                    "The production output capacity or atomic recipe budget is exhausted.");

            var writes = new List<NCPersistentAggregateWrite>(inputs.Count + outputCount + 4);
            var nextInputIds = inputNode.Snapshot.CommodityBatchIds.ToHashSet();
            foreach (var (allocation, batch) in inputs)
            {
                var eventId = Guid.NewGuid();
                var remaining = checked(batch.Snapshot.Quantity - allocation.Quantity);
                var next = batch.Snapshot with
                {
                    Quantity = remaining,
                    ReservedQuantity = checked(batch.Snapshot.ReservedQuantity - allocation.Quantity),
                    Location = remaining == 0
                        ? TerminalLocation(NCEconomicAssetLocationKind.Consumed)
                        : batch.Snapshot.Location,
                    Lifecycle = remaining == 0
                        ? NCEconomicAssetLifecycle.Consumed
                        : NCEconomicAssetLifecycle.Active,
                    UpdatedAt = now,
                    DataVersion = checked(batch.Snapshot.DataVersion + 1),
                    LastAppliedEventId = eventId,
                };
                if (remaining == 0)
                    nextInputIds.Remove(batch.Snapshot.BatchId);
                writes.Add(Write(batch, next, eventId,
                    "living-city.commodity-batch.production-consumed", "production-scheduler"));
            }

            var nextOutputBatchIds = outputNode.Snapshot.CommodityBatchIds.ToList();
            var nextOutputItemIds = outputNode.Snapshot.ItemIds.ToList();
            var outputOrdinal = 0;
            foreach (var output in recipe.CommodityOutputs)
            {
                var definition = _prototypes.Index(output.Commodity);
                var quantity = checked(output.Quantity * job.Snapshot.Runs);
                if (quantity > definition.MaximumBatchQuantity)
                    return Reject(NCMarketMutationStatus.Rejected, "A recipe output exceeds its batch maximum.");
                var batchId = new NCCommodityBatchId(NCPersistentIdentity.Command(
                    "production", "commodity-output", $"{jobId}.{outputOrdinal}"));
                if (await _assets.GetCommodityBatchAsync(batchId, true) != null)
                    return Reject(NCMarketMutationStatus.VersionConflict, "A production output identity already exists.");
                var eventId = Guid.NewGuid();
                var snapshot = new NCCommodityBatchSnapshot(
                    batchId,
                    definition.ID,
                    quantity,
                    0,
                    output.Quality,
                    business.Snapshot.BusinessParty,
                    StockLocation(outputNode.Snapshot.StockNodeId),
                    definition.LegalStatus,
                    definition.TradeStatus,
                    NCEconomicAssetLifecycle.Active,
                    Provenance(jobId, business.Snapshot.BusinessParty, now),
                    definition.ShelfLife == TimeSpan.Zero ? null : now.Advance(definition.ShelfLife),
                    now,
                    now,
                    1,
                    eventId);
                writes.Add(Write(snapshot, eventId,
                    "living-city.commodity-batch.production-created", "production-scheduler"));
                nextOutputBatchIds.Add(batchId);
                outputOrdinal++;
            }
            foreach (var output in recipe.ItemOutputs)
            {
                var definition = _prototypes.Index(output.Definition);
                var quantity = checked(output.Quantity * job.Snapshot.Runs);
                for (var index = 0; index < quantity; index++)
                {
                    var itemId = new NCItemId(NCPersistentIdentity.Command(
                        "production", "item-output", $"{jobId}.{outputOrdinal}.{index}"));
                    if (await _assets.GetItemAsync(itemId, true) != null)
                        return Reject(NCMarketMutationStatus.VersionConflict,
                            "A production item identity already exists.");
                    var eventId = Guid.NewGuid();
                    var snapshot = new NCItemInstanceSnapshot(
                        itemId,
                        definition.ID,
                        $"{jobId}-{outputOrdinal:D2}-{index + 1:D4}",
                        output.Condition,
                        null,
                        business.Snapshot.BusinessParty,
                        StockLocation(outputNode.Snapshot.StockNodeId),
                        definition.LegalStatus,
                        definition.TradeStatus,
                        NCEconomicAssetLifecycle.Active,
                        Provenance(jobId, business.Snapshot.BusinessParty, now),
                        null,
                        [],
                        now,
                        now,
                        1,
                        eventId);
                    writes.Add(Write(snapshot, eventId,
                        "living-city.item-instance.production-created", "production-scheduler"));
                    nextOutputItemIds.Add(itemId);
                }
                outputOrdinal++;
            }

            var inputNodeEventId = Guid.NewGuid();
            var nextInputNode = inputNode.Snapshot with
            {
                CommodityBatchIds = nextInputIds.OrderBy(id => id.Value).ToArray(),
                UpdatedAt = now,
                DataVersion = checked(inputNode.Snapshot.DataVersion + 1),
                LastAppliedEventId = inputNodeEventId,
            };
            writes.Add(Write(inputNode, nextInputNode, inputNodeEventId,
                "living-city.stock-node.production-inputs-consumed", "production-scheduler"));
            var outputNodeEventId = Guid.NewGuid();
            var nextOutputNode = outputNode.Snapshot with
            {
                CommodityBatchIds = nextOutputBatchIds.OrderBy(id => id.Value).ToArray(),
                ItemIds = nextOutputItemIds.OrderBy(id => id.Value).ToArray(),
                UpdatedAt = now,
                DataVersion = checked(outputNode.Snapshot.DataVersion + 1),
                LastAppliedEventId = outputNodeEventId,
            };
            writes.Add(Write(outputNode, nextOutputNode, outputNodeEventId,
                "living-city.stock-node.production-outputs-added", "production-scheduler"));
            var businessEventId = Guid.NewGuid();
            var nextBusiness = business.Snapshot with
            {
                ProductionQueue = business.Snapshot.ProductionQueue.Where(id => id != jobId).ToArray(),
                Status = NCBusinessStatus.Active,
                UpdatedAt = now,
                DataVersion = checked(business.Snapshot.DataVersion + 1),
                LastAppliedEventId = businessEventId,
            };
            writes.Add(Write(business, nextBusiness, businessEventId,
                "living-city.business.production-completed", "production-scheduler"));
            var jobEventId = Guid.NewGuid();
            var nextJob = job.Snapshot with
            {
                Status = NCProductionJobStatus.Completed,
                UpdatedAt = now,
                DataVersion = checked(job.Snapshot.DataVersion + 1),
                LastAppliedEventId = jobEventId,
            };
            writes.Add(Write(job, nextJob, jobEventId,
                "living-city.production-job.completed", "production-scheduler"));
            var persisted = await _database.ApplyNCPersistentBatchMutationAsync(new NCPersistentBatchMutationRequest(
                NCPersistentIdentity.Command("production", "complete", jobId.ToString()),
                "living-city.production-job.complete",
                "production-job",
                jobId.ToString(),
                writes,
                JsonSerializer.Serialize(new Receipt(jobId, job.AggregateVersion + 1), JsonOptions),
                NCPersistentIdentity.Correlation("production", "complete", jobId.ToString()),
                job.Snapshot.LastAppliedEventId,
                epoch,
                now,
                []));
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return Reject(Map(persisted.Status), persisted.Error ?? "Production completion was not committed.");
            await RefreshCompletionParticipantsAsync(job, business, inputNode, outputNode, inputs);
            var loaded = await GetAsync(jobId, true);
            var loadedBusiness = await _businesses.GetAsync(job.Snapshot.BusinessId, true);
            return loaded == null || loadedBusiness == null
                ? Reject(NCMarketMutationStatus.StorageFailure,
                    "The completed production state could not be reloaded.")
                : new(persisted.Status == NCPersistentMutationStatus.Applied
                    ? NCMarketMutationStatus.Applied
                    : NCMarketMutationStatus.Duplicate, loaded, loadedBusiness, null);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to complete production job {jobId}: {exception}");
            return Reject(NCMarketMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<NCProductionMutationResult> CloseWithoutOutputsAsync(
        Guid commandId,
        NCProductionJobId jobId,
        long expectedVersion,
        NCProductionJobStatus status,
        string failureCode,
        NCPartyRef initiator,
        Guid correlationId,
        Guid? causationId,
        string auditSource,
        NCSessionEpoch epoch,
        NCActiveWorldTime now)
    {
        var gate = Gate(jobId.Value);
        await gate.WaitAsync();
        try
        {
            var job = await GetAsync(jobId, true);
            if (job == null)
                return Reject(NCMarketMutationStatus.NotFound, "The production job does not exist.");
            if (job.AggregateVersion != expectedVersion)
                return Reject(NCMarketMutationStatus.VersionConflict, "The production job version changed.");
            var business = await _businesses.GetAsync(job.Snapshot.BusinessId, true);
            if (business == null || initiator != business.Snapshot.BusinessParty &&
                initiator != business.Snapshot.Owner && !business.Snapshot.Operators.Contains(initiator))
                return Reject(NCMarketMutationStatus.Rejected, "The initiator cannot close this production job.");
            var node = await _assets.GetStockNodeAsync(business.Snapshot.InventoryStockNodeId, true);
            if (node == null)
                return Reject(NCMarketMutationStatus.StorageFailure, "The production input stock is unavailable.");
            var writes = new List<NCPersistentAggregateWrite>();
            var batches = new List<NCCommodityBatchRecord>();
            foreach (var allocation in job.Snapshot.Inputs)
            {
                var batch = await _assets.GetCommodityBatchAsync(allocation.BatchId, true);
                if (batch == null || batch.Snapshot.ReservedQuantity < allocation.Quantity)
                    return Reject(NCMarketMutationStatus.VersionConflict,
                        "A production reservation cannot be released safely.");
                batches.Add(batch);
                var eventId = Guid.NewGuid();
                var next = batch.Snapshot with
                {
                    ReservedQuantity = checked(batch.Snapshot.ReservedQuantity - allocation.Quantity),
                    UpdatedAt = now,
                    DataVersion = checked(batch.Snapshot.DataVersion + 1),
                    LastAppliedEventId = eventId,
                };
                writes.Add(Write(batch, next, eventId,
                    "living-city.commodity-batch.production-released", auditSource));
            }
            var nodeEventId = Guid.NewGuid();
            var nextNode = node.Snapshot with
            {
                UpdatedAt = now,
                DataVersion = checked(node.Snapshot.DataVersion + 1),
                LastAppliedEventId = nodeEventId,
            };
            writes.Add(Write(node, nextNode, nodeEventId,
                "living-city.stock-node.production-released", auditSource));
            var businessEventId = Guid.NewGuid();
            var nextBusiness = business.Snapshot with
            {
                ProductionQueue = business.Snapshot.ProductionQueue.Where(id => id != jobId).ToArray(),
                UpdatedAt = now,
                DataVersion = checked(business.Snapshot.DataVersion + 1),
                LastAppliedEventId = businessEventId,
            };
            writes.Add(Write(business, nextBusiness, businessEventId,
                "living-city.business.production-closed", auditSource));
            var jobEventId = Guid.NewGuid();
            var nextJob = job.Snapshot with
            {
                Status = status,
                FailureCode = failureCode,
                UpdatedAt = now,
                DataVersion = checked(job.Snapshot.DataVersion + 1),
                LastAppliedEventId = jobEventId,
            };
            writes.Add(Write(job, nextJob, jobEventId,
                "living-city.production-job.closed", auditSource));
            var persisted = await _database.ApplyNCPersistentBatchMutationAsync(new NCPersistentBatchMutationRequest(
                commandId, "living-city.production-job.close", "production-job", jobId.ToString(), writes,
                JsonSerializer.Serialize(new Receipt(jobId, job.AggregateVersion + 1), JsonOptions),
                correlationId, causationId, epoch, now, []));
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return Reject(Map(persisted.Status), persisted.Error ?? "The production job was not closed.");
            foreach (var batch in batches)
                await _assets.GetCommodityBatchAsync(batch.Snapshot.BatchId, true);
            await _assets.GetStockNodeAsync(node.Snapshot.StockNodeId, true);
            var loaded = await GetAsync(jobId, true);
            var loadedBusiness = await _businesses.GetAsync(job.Snapshot.BusinessId, true);
            return loaded == null || loadedBusiness == null
                ? Reject(NCMarketMutationStatus.StorageFailure, "The closed production job could not be reloaded.")
                : new(persisted.Status == NCPersistentMutationStatus.Applied
                    ? NCMarketMutationStatus.Applied
                    : NCMarketMutationStatus.Duplicate, loaded, loadedBusiness, null);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<List<SelectedInput>?> SelectInputsAsync(
        NCStockNodeRecord node,
        NCProductionRecipePrototype recipe,
        int runs,
        NCActiveWorldTime now)
    {
        var result = new List<SelectedInput>();
        foreach (var input in recipe.CommodityInputs)
        {
            var remaining = checked(input.Quantity * runs);
            foreach (var batchId in node.Snapshot.CommodityBatchIds.OrderBy(id => id.Value))
            {
                if (remaining == 0)
                    break;
                var batch = await _assets.GetCommodityBatchAsync(batchId, true);
                if (batch == null || batch.Snapshot.CommodityId != input.Commodity.Id ||
                    batch.Snapshot.Quality < input.MinimumQuality ||
                    batch.Snapshot.ExpiresAt is { } expiry && now >= expiry ||
                    batch.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active)
                    continue;
                var available = batch.Snapshot.Quantity - batch.Snapshot.ReservedQuantity;
                if (available <= 0)
                    continue;
                var quantity = Math.Min(available, remaining);
                result.Add(new SelectedInput(batch, quantity));
                remaining -= quantity;
            }
            if (remaining > 0)
                return null;
        }
        return result.Count <= MaximumCombinedRecipeEntries ? result : null;
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        _active = true;
        _generation++;
        _due.Clear();
        _queued.Clear();
        _processing.Clear();
        Track(RecoverAsync(_generation));
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        _active = false;
        _generation++;
        if (_inFlight.Count > 0)
            args.AddPendingWrite(Task.WhenAll(_inFlight.ToArray()));
    }

    private void OnSessionClosed(NCActiveWorldSessionClosedEvent args) => ClearRuntime();
    private void OnClockFailed(NCActiveWorldClockFailedEvent args) => ClearRuntime();

    private void ClearRuntime()
    {
        _active = false;
        _generation++;
        _due.Clear();
        _queued.Clear();
        _processing.Clear();
    }

    private async Task RecoverAsync(int generation)
    {
        string? after = null;
        var recovered = 0;
        while (_active && generation == _generation && recovered < MaximumRecoveredJobs)
        {
            var page = await _database.GetNCAggregateSnapshotPageAsync(AggregateType, after, RecoveryPageSize);
            if (!_active || generation != _generation)
                return;
            foreach (var stored in page)
            {
                if (!Guid.TryParse(stored.AggregateId, out var raw))
                    continue;
                var job = await GetAsync(new NCProductionJobId(raw), true);
                if (job?.Snapshot.Status == NCProductionJobStatus.InProgress)
                    Enqueue(job.Snapshot.JobId, job.Snapshot.DueAt);
                recovered++;
            }
            if (page.Count < RecoveryPageSize)
                return;
            after = page[^1].AggregateId;
        }
    }

    private void Enqueue(NCProductionJobId jobId, NCActiveWorldTime dueAt)
    {
        if (_active && _queued.Add(jobId))
            _due.Enqueue(jobId, dueAt.Ticks);
    }

    private void Track(Task task)
    {
        _inFlight.Add(task);
        _ = CompleteTrackedAsync(task);
    }

    private async Task CompleteTrackedAsync(Task task)
    {
        try
        {
            await task;
        }
        finally
        {
            _inFlight.Remove(task);
        }
    }

    private async Task RefreshParticipantsAsync(
        NCProductionJobSnapshot job,
        NCBusinessSnapshot business,
        IEnumerable<NCCommodityBatchRecord> batches)
    {
        foreach (var batch in batches)
            await _assets.GetCommodityBatchAsync(batch.Snapshot.BatchId, true);
        await _assets.GetStockNodeAsync(business.InventoryStockNodeId, true);
        await _businesses.GetAsync(job.BusinessId, true);
    }

    private async Task RefreshCompletionParticipantsAsync(
        NCProductionJobRecord job,
        NCBusinessRecord business,
        NCStockNodeRecord input,
        NCStockNodeRecord output,
        IEnumerable<(NCProductionInputAllocation Allocation, NCCommodityBatchRecord Batch)> inputs)
    {
        foreach (var (_, batch) in inputs)
            await _assets.GetCommodityBatchAsync(batch.Snapshot.BatchId, true);
        await _assets.GetStockNodeAsync(input.Snapshot.StockNodeId, true);
        var outputReloaded = await _assets.GetStockNodeAsync(output.Snapshot.StockNodeId, true);
        if (outputReloaded != null)
        {
            foreach (var batchId in outputReloaded.Snapshot.CommodityBatchIds)
                await _assets.GetCommodityBatchAsync(batchId, true);
            foreach (var itemId in outputReloaded.Snapshot.ItemIds)
                await _assets.GetItemAsync(itemId, true);
        }
        await _businesses.GetAsync(business.Snapshot.BusinessId, true);
        await GetAsync(job.Snapshot.JobId, true);
    }

    private bool TryPolicy(out NCMarketPolicyPrototype policy) =>
        _prototypes.TryIndex(DefaultPolicy, out policy!);

    private bool Valid(NCProductionRecipePrototype value, NCMarketPolicyPrototype policy) =>
        Stable(value.StableKey, 128) && value.Duration > TimeSpan.Zero && value.Duration <= TimeSpan.FromDays(7) &&
        value.CommodityInputs.Count is > 0 && value.CommodityInputs.Count <= policy.MaximumProductionInputs &&
        value.CommodityOutputs.Count + value.ItemOutputs.Count is > 0 &&
        value.CommodityOutputs.Count + value.ItemOutputs.Count <= policy.MaximumProductionOutputs &&
        value.CommodityInputs.Count + value.CommodityOutputs.Count +
        value.ItemOutputs.Sum(output => output.Quantity) <= MaximumCombinedRecipeEntries &&
        value.AllowedBusinessKinds.Count is > 0 && value.AllowedBusinessKinds.All(Enum.IsDefined) &&
        value.CommodityInputs.All(input => input.Quantity > 0 && input.MinimumQuality is >= 0 and <= 1000 &&
            _prototypes.HasIndex(input.Commodity)) &&
        value.CommodityOutputs.All(output => output.Quantity > 0 && output.Quality is >= 0 and <= 1000 &&
            _prototypes.HasIndex(output.Commodity)) &&
        value.ItemOutputs.All(output => output.Quantity is > 0 and <= 18 && output.Condition is >= 0 and <= 1000 &&
            _prototypes.HasIndex(output.Definition));

    private static bool Valid(NCStartProductionRequest value) => value.CommandId != Guid.Empty &&
        value.JobId.IsValid && value.BusinessId.IsValid && value.ExpectedBusinessVersion > 0 &&
        value.Runs is > 0 and <= 1000 && value.Initiator.IsValid && value.CorrelationId != Guid.Empty &&
        ValidAudit(value.AuditSource);

    private NCPersistentAggregateWrite Write(
        NCCommodityBatchRecord current,
        NCCommodityBatchSnapshot next,
        Guid eventId,
        string eventType,
        string audit) => new(eventId, eventType, NCEconomicAssetRegistrySystem.CommodityBatchAggregateType,
        next.BatchId.ToString(), current.AggregateVersion, NCEconomicAssetRegistrySystem.SnapshotFormatVersion,
        JsonSerializer.Serialize(next, JsonOptions), JsonSerializer.Serialize(new ProductionEvent(next.BatchId.ToString(), audit), JsonOptions));

    private NCPersistentAggregateWrite Write(
        NCCommodityBatchSnapshot next,
        Guid eventId,
        string eventType,
        string audit) => new(eventId, eventType, NCEconomicAssetRegistrySystem.CommodityBatchAggregateType,
        next.BatchId.ToString(), 0, NCEconomicAssetRegistrySystem.SnapshotFormatVersion,
        JsonSerializer.Serialize(next, JsonOptions), JsonSerializer.Serialize(new ProductionEvent(next.BatchId.ToString(), audit), JsonOptions));

    private NCPersistentAggregateWrite Write(
        NCItemInstanceSnapshot next,
        Guid eventId,
        string eventType,
        string audit) => new(eventId, eventType, NCEconomicAssetRegistrySystem.ItemInstanceAggregateType,
        next.ItemId.ToString(), 0, NCEconomicAssetRegistrySystem.SnapshotFormatVersion,
        JsonSerializer.Serialize(next, JsonOptions), JsonSerializer.Serialize(new ProductionEvent(next.ItemId.ToString(), audit), JsonOptions));

    private NCPersistentAggregateWrite Write(
        NCStockNodeRecord current,
        NCStockNodeSnapshot next,
        Guid eventId,
        string eventType,
        string audit) => new(eventId, eventType, NCEconomicAssetRegistrySystem.StockNodeAggregateType,
        next.StockNodeId.ToString(), current.AggregateVersion, NCEconomicAssetRegistrySystem.SnapshotFormatVersion,
        JsonSerializer.Serialize(next, JsonOptions), JsonSerializer.Serialize(new ProductionEvent(next.StockNodeId.ToString(), audit), JsonOptions));

    private NCPersistentAggregateWrite Write(
        NCBusinessRecord current,
        NCBusinessSnapshot next,
        Guid eventId,
        string eventType,
        string audit) => new(eventId, eventType, NCBusinessRegistrySystem.AggregateType,
        next.BusinessId.ToString(), current.AggregateVersion, SnapshotFormatVersion,
        JsonSerializer.Serialize(next, JsonOptions), JsonSerializer.Serialize(new ProductionEvent(next.BusinessId.ToString(), audit), JsonOptions));

    private NCPersistentAggregateWrite Write(
        NCProductionJobRecord? current,
        NCProductionJobSnapshot next,
        Guid eventId,
        string eventType,
        string audit) => new(eventId, eventType, AggregateType, next.JobId.ToString(),
        current?.AggregateVersion ?? 0, SnapshotFormatVersion, JsonSerializer.Serialize(next, JsonOptions),
        JsonSerializer.Serialize(new ProductionEvent(next.JobId.ToString(), audit), JsonOptions));

    private static NCProductionJobRecord? Deserialize(NCAggregateSnapshotData? stored, NCProductionJobId id)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion)
            return null;
        try
        {
            var snapshot = JsonSerializer.Deserialize<NCProductionJobSnapshot>(stored.Payload, JsonOptions);
            return snapshot?.JobId == id
                ? new(snapshot, stored.AggregateVersion, stored.SessionEpoch, stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NCEconomicAssetProvenance Provenance(
        NCProductionJobId jobId,
        NCPartyRef manufacturer,
        NCActiveWorldTime now) => new(
        $"production.{jobId}", manufacturer, [jobId.Value], now);
    private static NCEconomicAssetLocation StockLocation(NCStockNodeId id) =>
        new(NCEconomicAssetLocationKind.StockNode, id.ToString());
    private static NCEconomicAssetLocation TerminalLocation(NCEconomicAssetLocationKind kind) =>
        new(kind, kind == NCEconomicAssetLocationKind.Consumed ? "consumed" : "destroyed");
    private SemaphoreSlim Gate(Guid id) => _gates[(id.GetHashCode() & int.MaxValue) % _gates.Length];
    private static bool Stable(string value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max &&
        value == value.Trim().ToLowerInvariant();
    private static bool ValidAudit(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
    private static NCMarketMutationStatus Map(NCPersistentMutationStatus status) => status switch
    {
        NCPersistentMutationStatus.VersionConflict => NCMarketMutationStatus.VersionConflict,
        NCPersistentMutationStatus.ClockRejected => NCMarketMutationStatus.ClockUnavailable,
        _ => NCMarketMutationStatus.StorageFailure,
    };
    private static NCProductionMutationResult Reject(NCMarketMutationStatus status, string error) =>
        new(status, null, null, error);
    private sealed record SelectedInput(NCCommodityBatchRecord Batch, long Quantity);
    private sealed record ProductionEvent(string SubjectId, string AuditSource);
    private sealed record Receipt(NCProductionJobId JobId, long AggregateVersion);
}
