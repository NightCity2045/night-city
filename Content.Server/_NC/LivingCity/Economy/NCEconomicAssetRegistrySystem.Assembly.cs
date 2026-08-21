// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Economy;

/// <summary>
/// Owns abstract weapon-part composition. It records stable part identities and custody only; gun behavior, firing,
/// condition loss, and combat resolution remain in their existing authoritative systems.
/// </summary>
public sealed partial class NCEconomicAssetRegistrySystem
{
    private const string InstallItemPartCommand = "living-city.item-assembly.install";
    private const string RemoveItemPartCommand = "living-city.item-assembly.remove";

    public async Task<NCEconomicItemAssemblyMutationResult> InstallItemPartAsync(
        NCInstallEconomicItemPartRequest request)
    {
        if (!Valid(request) || !_clock.TryGetCurrentTime(out var epoch, out var now))
            return RejectAssembly(_clock.IsReady ? NCEconomicAssetMutationStatus.Rejected :
                NCEconomicAssetMutationStatus.ClockUnavailable, "The item-part install request is invalid.");
        if (!await ResolveParties(request.Owner, request.Initiator) || request.Initiator != request.Owner)
            return RejectAssembly(NCEconomicAssetMutationStatus.PartyUnavailable,
                "Only the resolved owner can change an abstract item assembly.");

        var command = await InspectCommand(request.CommandId, InstallItemPartCommand,
            "economic-item-assembly", Key(request.ParentItemId));
        if (command == InitialCommandState.Collision)
            return RejectAssembly(NCEconomicAssetMutationStatus.Rejected,
                "The command ID belongs to another operation.");
        var gates = await AcquireGates(
            request.ParentItemId.Value, request.PartItemId.Value, request.StockNodeId.Value);
        try
        {
            var parent = await GetItemAsync(request.ParentItemId, true);
            var part = await GetItemAsync(request.PartItemId, true);
            var node = await GetStockNodeAsync(request.StockNodeId, true);
            if (parent == null || part == null || node == null)
                return RejectAssembly(NCEconomicAssetMutationStatus.NotFound,
                    "The parent item, part, or stock node does not exist.");
            if (parent.Snapshot.InstalledPartIds.Contains(request.PartItemId) &&
                part.Snapshot.ParentItemId == request.ParentItemId)
            {
                return command == InitialCommandState.Completed
                    ? new(NCEconomicAssetMutationStatus.Duplicate, parent, part, node, null)
                    : RejectAssembly(NCEconomicAssetMutationStatus.Rejected,
                        "The part is already installed by another command.");
            }
            if (command is InitialCommandState.InProgress or InitialCommandState.Completed)
                return RejectAssembly(NCEconomicAssetMutationStatus.StorageFailure,
                    "The completed or in-progress command does not match the current assembly.");
            var stateError = ValidateInstallState(request, parent, part, node);
            if (stateError != null)
                return stateError;

            var parentEventId = Guid.NewGuid();
            var nextParent = parent.Snapshot with
            {
                InstalledPartIds = parent.Snapshot.InstalledPartIds.Append(request.PartItemId)
                    .OrderBy(id => id.Value).ToArray(),
                UpdatedAt = now,
                DataVersion = checked(parent.Snapshot.DataVersion + 1),
                LastAppliedEventId = parentEventId,
            };
            var partEventId = Guid.NewGuid();
            var nextPart = part.Snapshot with
            {
                ParentItemId = request.ParentItemId,
                Location = new NCEconomicAssetLocation(
                    NCEconomicAssetLocationKind.InstalledInItem, request.ParentItemId.ToString()),
                UpdatedAt = now,
                DataVersion = checked(part.Snapshot.DataVersion + 1),
                LastAppliedEventId = partEventId,
            };
            var nodeEventId = Guid.NewGuid();
            var nextNode = node.Snapshot with
            {
                ItemIds = node.Snapshot.ItemIds.Where(id => id != request.PartItemId).ToArray(),
                UpdatedAt = now,
                DataVersion = checked(node.Snapshot.DataVersion + 1),
                LastAppliedEventId = nodeEventId,
            };
            var persisted = await Persist(request.CommandId, InstallItemPartCommand, "economic-item-assembly",
                Key(request.ParentItemId),
                [
                    ItemMutationWrite(parent, nextParent, parentEventId,
                        "living-city.item-assembly.parent-updated", request.AuditSource),
                    ItemMutationWrite(part, nextPart, partEventId,
                        "living-city.item-assembly.part-installed", request.AuditSource),
                    StockNodeWrite(node, nextNode, nodeEventId,
                        "living-city.stock-node.part-installed", request.AuditSource),
                ],
                new AssetReceipt(Key(request.ParentItemId), parent.AggregateVersion + 1),
                request.CorrelationId, request.CausationId, epoch, now);
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return RejectAssembly(MapPersistence(persisted.Status),
                    persisted.Error ?? "The item assembly was not committed.");
            return await LoadAssemblyResult(request.ParentItemId, request.PartItemId, request.StockNodeId,
                persisted.Status == NCPersistentMutationStatus.Applied
                    ? NCEconomicAssetMutationStatus.Applied
                    : NCEconomicAssetMutationStatus.Duplicate);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to install economic part {request.PartItemId}: {exception}");
            return RejectAssembly(NCEconomicAssetMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            ReleaseGates(gates);
        }
    }

    public async Task<NCEconomicItemAssemblyMutationResult> RemoveItemPartAsync(
        NCRemoveEconomicItemPartRequest request)
    {
        if (!Valid(request) || !TryGetPolicy(out var policy) ||
            !_clock.TryGetCurrentTime(out var epoch, out var now))
            return RejectAssembly(_clock.IsReady ? NCEconomicAssetMutationStatus.Rejected :
                NCEconomicAssetMutationStatus.ClockUnavailable, "The item-part removal request is invalid.");
        if (!await ResolveParties(request.Owner, request.Initiator) || request.Initiator != request.Owner)
            return RejectAssembly(NCEconomicAssetMutationStatus.PartyUnavailable,
                "Only the resolved owner can change an abstract item assembly.");

        var command = await InspectCommand(request.CommandId, RemoveItemPartCommand,
            "economic-item-assembly", Key(request.ParentItemId));
        if (command == InitialCommandState.Collision)
            return RejectAssembly(NCEconomicAssetMutationStatus.Rejected,
                "The command ID belongs to another operation.");
        var gates = await AcquireGates(
            request.ParentItemId.Value, request.PartItemId.Value, request.DestinationStockNodeId.Value);
        try
        {
            var parent = await GetItemAsync(request.ParentItemId, true);
            var part = await GetItemAsync(request.PartItemId, true);
            var node = await GetStockNodeAsync(request.DestinationStockNodeId, true);
            if (parent == null || part == null || node == null)
                return RejectAssembly(NCEconomicAssetMutationStatus.NotFound,
                    "The parent item, part, or destination stock node does not exist.");
            if (!parent.Snapshot.InstalledPartIds.Contains(request.PartItemId) &&
                part.Snapshot.ParentItemId == null && node.Snapshot.ItemIds.Contains(request.PartItemId))
            {
                return command == InitialCommandState.Completed
                    ? new(NCEconomicAssetMutationStatus.Duplicate, parent, part, node, null)
                    : RejectAssembly(NCEconomicAssetMutationStatus.Rejected,
                        "The part was already removed by another command.");
            }
            if (command is InitialCommandState.InProgress or InitialCommandState.Completed)
                return RejectAssembly(NCEconomicAssetMutationStatus.StorageFailure,
                    "The completed or in-progress command does not match the current assembly.");
            var stateError = ValidateRemoveState(request, parent, part, node, policy);
            if (stateError != null)
                return stateError;

            var parentEventId = Guid.NewGuid();
            var nextParent = parent.Snapshot with
            {
                InstalledPartIds = parent.Snapshot.InstalledPartIds.Where(id => id != request.PartItemId).ToArray(),
                UpdatedAt = now,
                DataVersion = checked(parent.Snapshot.DataVersion + 1),
                LastAppliedEventId = parentEventId,
            };
            var partEventId = Guid.NewGuid();
            var nextPart = part.Snapshot with
            {
                ParentItemId = null,
                Location = StockLocation(request.DestinationStockNodeId),
                UpdatedAt = now,
                DataVersion = checked(part.Snapshot.DataVersion + 1),
                LastAppliedEventId = partEventId,
            };
            var nodeEventId = Guid.NewGuid();
            var nextNode = node.Snapshot with
            {
                ItemIds = node.Snapshot.ItemIds.Append(request.PartItemId).OrderBy(id => id.Value).ToArray(),
                UpdatedAt = now,
                DataVersion = checked(node.Snapshot.DataVersion + 1),
                LastAppliedEventId = nodeEventId,
            };
            var persisted = await Persist(request.CommandId, RemoveItemPartCommand, "economic-item-assembly",
                Key(request.ParentItemId),
                [
                    ItemMutationWrite(parent, nextParent, parentEventId,
                        "living-city.item-assembly.parent-updated", request.AuditSource),
                    ItemMutationWrite(part, nextPart, partEventId,
                        "living-city.item-assembly.part-removed", request.AuditSource),
                    StockNodeWrite(node, nextNode, nodeEventId,
                        "living-city.stock-node.part-removed", request.AuditSource),
                ],
                new AssetReceipt(Key(request.ParentItemId), parent.AggregateVersion + 1),
                request.CorrelationId, request.CausationId, epoch, now);
            if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
                return RejectAssembly(MapPersistence(persisted.Status),
                    persisted.Error ?? "The item disassembly was not committed.");
            return await LoadAssemblyResult(request.ParentItemId, request.PartItemId,
                request.DestinationStockNodeId,
                persisted.Status == NCPersistentMutationStatus.Applied
                    ? NCEconomicAssetMutationStatus.Applied
                    : NCEconomicAssetMutationStatus.Duplicate);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to remove economic part {request.PartItemId}: {exception}");
            return RejectAssembly(NCEconomicAssetMutationStatus.StorageFailure, exception.Message);
        }
        finally
        {
            ReleaseGates(gates);
        }
    }

    private NCEconomicItemAssemblyMutationResult? ValidateInstallState(
        NCInstallEconomicItemPartRequest request,
        NCItemInstanceRecord parent,
        NCItemInstanceRecord part,
        NCStockNodeRecord node)
    {
        if (_materializationLeases.IsLeased(request.StockNodeId))
            return RejectAssembly(NCEconomicAssetMutationStatus.Rejected,
                "A materialized assembly requires a physical workbench interaction.");
        if (parent.AggregateVersion != request.ExpectedParentVersion ||
            part.AggregateVersion != request.ExpectedPartVersion || node.AggregateVersion != request.ExpectedStockNodeVersion)
            return RejectAssembly(NCEconomicAssetMutationStatus.VersionConflict, "An assembly version changed.");
        if (!_prototypes.TryIndex(new ProtoId<NCItemDefinitionPrototype>(parent.Snapshot.DefinitionId),
                out var parentDefinition) ||
            !_prototypes.TryIndex(new ProtoId<NCItemDefinitionPrototype>(part.Snapshot.DefinitionId), out var partDefinition) ||
            parentDefinition.Domain != NCEconomicAssetDomain.WeaponAssembly ||
            partDefinition.Domain != NCEconomicAssetDomain.WeaponAssembly ||
            !parentDefinition.AllowedInstalledPartDefinitions.Contains(
                new ProtoId<NCItemDefinitionPrototype>(partDefinition.ID)))
        {
            return RejectAssembly(NCEconomicAssetMutationStatus.Rejected,
                "The authored weapon assembly does not accept this part definition.");
        }
        if (parent.Snapshot.Owner != request.Owner || part.Snapshot.Owner != request.Owner ||
            node.Snapshot.Owner != request.Owner || parent.Snapshot.Location != StockLocation(request.StockNodeId) ||
            part.Snapshot.Location != StockLocation(request.StockNodeId) ||
            !node.Snapshot.ItemIds.Contains(parent.Snapshot.ItemId) ||
            !node.Snapshot.ItemIds.Contains(part.Snapshot.ItemId) || parent.Snapshot.ReservationId.HasValue ||
            part.Snapshot.ReservationId.HasValue || parent.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active ||
            part.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active || part.Snapshot.ParentItemId.HasValue ||
            part.Snapshot.InstalledPartIds.Count > 0 ||
            parent.Snapshot.InstalledPartIds.Count >= parentDefinition.MaximumInstalledParts)
        {
            return RejectAssembly(NCEconomicAssetMutationStatus.Rejected,
                "The weapon, part, ownership, stock index, or lifecycle state is invalid.");
        }
        return null;
    }

    private NCEconomicItemAssemblyMutationResult? ValidateRemoveState(
        NCRemoveEconomicItemPartRequest request,
        NCItemInstanceRecord parent,
        NCItemInstanceRecord part,
        NCStockNodeRecord node,
        NCEconomicAssetPolicyPrototype policy)
    {
        if (_materializationLeases.IsLeased(request.DestinationStockNodeId))
            return RejectAssembly(NCEconomicAssetMutationStatus.Rejected,
                "A materialized assembly requires a physical workbench interaction.");
        if (parent.AggregateVersion != request.ExpectedParentVersion ||
            part.AggregateVersion != request.ExpectedPartVersion || node.AggregateVersion != request.ExpectedStockNodeVersion)
            return RejectAssembly(NCEconomicAssetMutationStatus.VersionConflict, "An assembly version changed.");
        if (parent.Snapshot.Owner != request.Owner || part.Snapshot.Owner != request.Owner ||
            node.Snapshot.Owner != request.Owner || parent.Snapshot.Location != StockLocation(request.DestinationStockNodeId) ||
            !node.Snapshot.ItemIds.Contains(parent.Snapshot.ItemId) ||
            !parent.Snapshot.InstalledPartIds.Contains(request.PartItemId) ||
            part.Snapshot.ParentItemId != request.ParentItemId ||
            part.Snapshot.Location != new NCEconomicAssetLocation(
                NCEconomicAssetLocationKind.InstalledInItem, request.ParentItemId.ToString()) ||
            part.Snapshot.ReservationId.HasValue || part.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active ||
            node.Snapshot.ItemIds.Contains(request.PartItemId) || !node.Snapshot.AcceptsDeposits ||
            node.Snapshot.ItemIds.Count >= policy.MaximumItemsPerStockNode)
        {
            return RejectAssembly(NCEconomicAssetMutationStatus.Rejected,
                "The weapon, installed part, ownership, or destination stock state is invalid.");
        }
        return null;
    }

    private async Task<NCEconomicItemAssemblyMutationResult> LoadAssemblyResult(
        NCItemId parentId,
        NCItemId partId,
        NCStockNodeId nodeId,
        NCEconomicAssetMutationStatus status)
    {
        var parentTask = GetItemAsync(parentId, true);
        var partTask = GetItemAsync(partId, true);
        var nodeTask = GetStockNodeAsync(nodeId, true);
        await Task.WhenAll(parentTask, partTask, nodeTask);
        var parent = await parentTask;
        var part = await partTask;
        var node = await nodeTask;
        return parent == null || part == null || node == null
            ? RejectAssembly(NCEconomicAssetMutationStatus.StorageFailure,
                "The committed assembly state could not be reloaded.")
            : new NCEconomicItemAssemblyMutationResult(status, parent, part, node, null);
    }

    /// <summary>
    /// Loads and validates every child before a parent ownership transfer. Returning null fails the whole atomic
    /// operation, so an incomplete assembly can never split ownership between parties.
    /// </summary>
    private async Task<IReadOnlyList<NCItemInstanceRecord>?> LoadAssemblyPartsAsync(NCItemInstanceRecord parent)
    {
        var result = new List<NCItemInstanceRecord>(parent.Snapshot.InstalledPartIds.Count);
        foreach (var partId in parent.Snapshot.InstalledPartIds.OrderBy(id => id.Value))
        {
            var part = await GetItemAsync(partId, true);
            if (part == null || part.Snapshot.ParentItemId != parent.Snapshot.ItemId ||
                part.Snapshot.Owner != parent.Snapshot.Owner ||
                part.Snapshot.Location != new NCEconomicAssetLocation(
                    NCEconomicAssetLocationKind.InstalledInItem, parent.Snapshot.ItemId.ToString()) ||
                part.Snapshot.ReservationId.HasValue ||
                part.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active)
                return null;
            result.Add(part);
        }
        return result;
    }

    private static bool Valid(NCInstallEconomicItemPartRequest value) =>
        value.CommandId != Guid.Empty && value.ParentItemId.IsValid && value.PartItemId.IsValid &&
        value.ParentItemId != value.PartItemId && value.ExpectedParentVersion > 0 && value.ExpectedPartVersion > 0 &&
        value.StockNodeId.IsValid && value.ExpectedStockNodeVersion > 0 && value.Owner.IsValid &&
        value.Initiator.IsValid && value.CorrelationId != Guid.Empty && ValidAudit(value.AuditSource);

    private static bool Valid(NCRemoveEconomicItemPartRequest value) =>
        value.CommandId != Guid.Empty && value.ParentItemId.IsValid && value.PartItemId.IsValid &&
        value.ParentItemId != value.PartItemId && value.ExpectedParentVersion > 0 && value.ExpectedPartVersion > 0 &&
        value.DestinationStockNodeId.IsValid && value.ExpectedStockNodeVersion > 0 && value.Owner.IsValid &&
        value.Initiator.IsValid && value.CorrelationId != Guid.Empty && ValidAudit(value.AuditSource);

    private static NCEconomicItemAssemblyMutationResult RejectAssembly(
        NCEconomicAssetMutationStatus status,
        string error) => new(status, null, null, null, error);
}
