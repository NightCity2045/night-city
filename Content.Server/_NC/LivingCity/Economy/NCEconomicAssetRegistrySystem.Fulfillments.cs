// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Threading.Tasks;
using Content.Server.Database;
using Content.Shared._NC.LivingCity.Economy;

namespace Content.Server._NC.LivingCity.Economy;

/// <summary>
/// Owns durable service-obligation transitions. Domain systems report only the outcome; they cannot alter prices,
/// ledger legs, parties, or information-access grants through this boundary.
/// </summary>
public sealed partial class NCEconomicAssetRegistrySystem
{
    private const string CompleteFulfillmentCommand = "living-city.economic-fulfillment.complete";

    public async Task<NCEconomicFulfillmentMutationResult> CompleteFulfillmentAsync(
        NCCompleteEconomicFulfillmentRequest request)
    {
        if (!Valid(request) || !_clock.TryGetCurrentTime(out var epoch, out var now))
        {
            return RejectFulfillment(_clock.IsReady
                ? NCEconomicAssetMutationStatus.Rejected
                : NCEconomicAssetMutationStatus.ClockUnavailable,
                "The fulfillment request or active-world clock is invalid.");
        }

        try
        {
            var command = await InspectCommand(request.CommandId, CompleteFulfillmentCommand,
                "economic-fulfillment", Key(request.FulfillmentId));
            if (command == InitialCommandState.Collision)
                return RejectFulfillment(NCEconomicAssetMutationStatus.Rejected,
                    "The command ID belongs to another operation.");
            if (command == InitialCommandState.InProgress)
                return RejectFulfillment(NCEconomicAssetMutationStatus.StorageFailure,
                    "The fulfillment command is still in progress.");

            var gate = Gate(request.FulfillmentId.Value);
            await gate.WaitAsync();
            try
            {
                var current = await GetFulfillmentAsync(request.FulfillmentId, true);
                if (current == null)
                    return RejectFulfillment(NCEconomicAssetMutationStatus.NotFound,
                        "The fulfillment does not exist.");
                if (!await ResolveParties(current.Snapshot.Provider, current.Snapshot.Recipient, request.Initiator))
                    return RejectFulfillment(NCEconomicAssetMutationStatus.PartyUnavailable,
                        "The fulfillment parties or initiator are unavailable.");
                if (current.Snapshot.OfferKind != NCEconomicOfferKind.Service ||
                    request.Initiator != current.Snapshot.Provider)
                {
                    return RejectFulfillment(NCEconomicAssetMutationStatus.Rejected,
                        "Only the service provider can complete or fail a paid service obligation.");
                }
                if (current.Snapshot.Status != NCEconomicFulfillmentStatus.Pending)
                {
                    return command == InitialCommandState.Completed && current.Snapshot.Status == request.FinalStatus
                        ? new NCEconomicFulfillmentMutationResult(
                            NCEconomicAssetMutationStatus.Duplicate, current, null)
                        : RejectFulfillment(NCEconomicAssetMutationStatus.Rejected,
                            "The service obligation is no longer pending.");
                }
                if (current.AggregateVersion != request.ExpectedFulfillmentVersion)
                    return RejectFulfillment(NCEconomicAssetMutationStatus.VersionConflict,
                        "The fulfillment version changed.");

                var eventId = Guid.NewGuid();
                var next = current.Snapshot with
                {
                    Status = request.FinalStatus,
                    UpdatedAt = now,
                    DataVersion = checked(current.Snapshot.DataVersion + 1),
                    LastAppliedEventId = eventId,
                };
                var persisted = await Persist(
                    command == InitialCommandState.Available ? request.CommandId : Guid.NewGuid(),
                    CompleteFulfillmentCommand,
                    "economic-fulfillment",
                    Key(request.FulfillmentId),
                    [FulfillmentWrite(current, next, eventId,
                        request.FinalStatus == NCEconomicFulfillmentStatus.Completed
                            ? "living-city.economic-fulfillment.completed"
                            : "living-city.economic-fulfillment.failed",
                        request.AuditSource)],
                    new AssetReceipt(Key(request.FulfillmentId), current.AggregateVersion + 1),
                    request.CorrelationId,
                    request.CausationId,
                    epoch,
                    now);
                if (persisted.Status is not (NCPersistentMutationStatus.Applied or
                    NCPersistentMutationStatus.DuplicateCompleted))
                {
                    return RejectFulfillment(MapPersistence(persisted.Status),
                        persisted.Error ?? "The fulfillment outcome was not committed.");
                }

                var loaded = await GetFulfillmentAsync(request.FulfillmentId, true);
                return loaded == null
                    ? RejectFulfillment(NCEconomicAssetMutationStatus.StorageFailure,
                        "The committed fulfillment could not be reloaded.")
                    : new NCEconomicFulfillmentMutationResult(
                        persisted.Status == NCPersistentMutationStatus.Applied
                            ? NCEconomicAssetMutationStatus.Applied
                            : NCEconomicAssetMutationStatus.Duplicate,
                        loaded,
                        null);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to complete economic fulfillment {request.FulfillmentId}: {exception}");
            return RejectFulfillment(NCEconomicAssetMutationStatus.StorageFailure, exception.Message);
        }
    }

    private static bool Valid(NCCompleteEconomicFulfillmentRequest value) =>
        value.CommandId != Guid.Empty && value.FulfillmentId.IsValid && value.ExpectedFulfillmentVersion > 0 &&
        value.FinalStatus is NCEconomicFulfillmentStatus.Completed or NCEconomicFulfillmentStatus.Failed &&
        value.Initiator.IsValid && value.CorrelationId != Guid.Empty && ValidAudit(value.AuditSource);

    private static NCEconomicAssetMutationStatus MapPersistence(NCPersistentMutationStatus status) => status switch
    {
        NCPersistentMutationStatus.DuplicateCompleted => NCEconomicAssetMutationStatus.Duplicate,
        NCPersistentMutationStatus.VersionConflict => NCEconomicAssetMutationStatus.VersionConflict,
        NCPersistentMutationStatus.DuplicateFailed => NCEconomicAssetMutationStatus.VersionConflict,
        _ => NCEconomicAssetMutationStatus.StorageFailure,
    };

    private static NCEconomicFulfillmentMutationResult RejectFulfillment(
        NCEconomicAssetMutationStatus status,
        string error) => new(status, null, error);
}
