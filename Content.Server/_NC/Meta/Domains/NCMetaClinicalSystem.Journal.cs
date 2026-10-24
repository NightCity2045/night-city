// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Server.Database;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Domains;

public sealed partial class NCMetaClinicalSystem
{
    private const string ReceiptType = "meta.clinical.operation";
    private static readonly JsonSerializerOptions ReceiptJson = new() { MaxDepth = 8 };
    private int _statusReads;

    private async Task<MetaOperationReceipt?> LoadReceiptAsync(Guid id, string owner, string deviceId, string kind)
    {
        var snapshot = await _persistence.ReadSnapshotAsync(ReceiptType, id.ToString("D"));
        if (snapshot == null) return null;
        if (snapshot.Payload.Length > 8192 || snapshot.FormatVersion != 1) throw new InvalidDataException("Invalid clinical receipt.");
        var receipt = JsonSerializer.Deserialize<MetaOperationReceipt>(snapshot.Payload, ReceiptJson);
        if (receipt == null || receipt.Id != id.ToString("N") || receipt.Owner != owner || receipt.DeviceId != deviceId ||
            receipt.Kind != kind || receipt.Fingerprint.Length != 64 || receipt.Revision != snapshot.AggregateVersion ||
            !Enum.IsDefined(receipt.Status)) throw new InvalidDataException("Clinical receipt scope mismatch.");
        return receipt;
    }

    private Task<bool> SaveReceiptAsync(Operation operation, MetaOperationReceipt receipt) => SaveReceiptAsync(operation.Id, operation.Epoch, operation.Time, receipt);
    private async Task<bool> SaveReceiptAsync(Guid id, NCSessionEpoch epoch, NCActiveWorldTime time, MetaOperationReceipt receipt)
    {
        var requestId = NCMetaPersistenceSystem.StableGuid("clinical-receipt:" + receipt.Id + ":" + receipt.Revision);
        var payload = JsonSerializer.Serialize(receipt, ReceiptJson);
        var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(requestId,
            "meta.clinical.transition", "meta.owner", receipt.Owner, Guid.NewGuid(), "meta.clinical.transitioned",
            ReceiptType, id.ToString("D"), receipt.Revision - 1, 1, payload, "{}", payload,
            id, null, epoch, time, [],
            RequestFingerprint: MetaOperationReceipt.HashFields(receipt.Fingerprint, receipt.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), receipt.Status.ToString(), receipt.ErrorCode)));
        return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted;
    }

    private async Task ExecuteAsync(Operation operation, IDisposable lease)
    {
        var invocation = operation.Invocation;
        try
        {
            var existing = await LoadReceiptAsync(operation.Id, invocation.Runtime.Owner, invocation.DeviceId, operation.Kind);
            if (existing != null)
            {
                if (existing.Fingerprint != operation.Fingerprint)
                {
                    await OnMainThread(() => _runtime.TryComplete(invocation.Runtime, invocation.OperationId, _ => Error(invocation, "Conflict"))); return;
                }
                var recovered = existing.ReconcileInterrupted();
                if (recovered != existing && !await SaveReceiptAsync(operation, recovered)) throw new IOException("Cannot reconcile clinical receipt.");
                await OnMainThread(() => CompleteReceipt(invocation, recovered)); return;
            }
            var receipt = new MetaOperationReceipt(operation.Id.ToString("N"), invocation.Runtime.Owner, invocation.DeviceId,
                operation.Kind, operation.Fingerprint, MetaOperationStatus.Prepared, "", 1);
            if (!await SaveReceiptAsync(operation, receipt)) throw new IOException("Cannot prepare clinical operation.");
            receipt = receipt.Transition(MetaOperationStatus.Committing);
            if (!await SaveReceiptAsync(operation, receipt)) throw new IOException("Cannot admit clinical operation.");
            // No world mutation occurs before the Committing marker is durable. This callback runs only on the server thread.
            var outcome = await OnMainThread(() => ApplyNative(operation));
            receipt = receipt.Transition(outcome.Item1, outcome.Item2);
            if (!await SaveReceiptAsync(operation, receipt)) throw new IOException("Cannot commit clinical outcome.");
            operation.Receipt = receipt;
            await OnMainThread(() => CompleteReceipt(invocation, receipt));
            if (receipt.Status != MetaOperationStatus.Running) return;

            // A native DoAfter owns timing and cancellation. A world snapshot may retain Running; restore reconciles it conservatively.
            lease.Dispose();
            var completed = await operation.NativeCompletion.Task;
            var finalLease = _persistence.TryBeginNativeMutation();
            if (finalLease == null) return;
            using (finalLease)
            {
                receipt = receipt.Transition(completed.Status, completed.Error);
                if (!await SaveReceiptAsync(operation, receipt)) throw new IOException("Cannot settle timed clinical operation.");
                operation.Receipt = receipt;
                await OnMainThread(() => _runtime.TryPublishEvent(invocation.Device, "meta.medical.ProcedureController.operationChanged", process => ReceiptValue(process, receipt)));
            }
        }
        catch (Exception exception)
        {
            Log.Error($"META clinical operation {operation.Id} requires reconciliation: {exception}");
            await OnMainThread(() => _runtime.TryComplete(invocation.Runtime, invocation.OperationId, _ => Error(invocation, "OutcomeUnknown")));
        }
        finally
        {
            lease.Dispose();
            _admissions.Enqueue(() => _operations.Remove(operation.Id));
        }
    }

    private async Task ReadStatusAsync(NCMetaDeviceInvocation invocation, Guid id, string kind, NCSessionEpoch epoch, NCActiveWorldTime time)
    {
        try
        {
            var receipt = await LoadReceiptAsync(id, invocation.Runtime.Owner, invocation.DeviceId, kind);
            // A live native operation has a definitive callback pending. An orphaned in-flight record requires domain reconciliation.
            var orphan = await OnMainThread(() => _runtime.CanContinue(invocation) && receipt != null && !receipt.Terminal && !_operations.ContainsKey(id));
            if (orphan)
            {
                using var lease = _persistence.TryBeginNativeMutation();
                if (lease == null) { await OnMainThread(() => _runtime.TryComplete(invocation.Runtime, invocation.OperationId, _ => Error(invocation, "Busy"))); return; }
                receipt = receipt!.ReconcileInterrupted();
                if (!await SaveReceiptAsync(id, epoch, time, receipt)) throw new IOException("Cannot reconcile orphaned clinical operation.");
            }
            await OnMainThread(() => receipt == null ? _runtime.CanContinue(invocation) &&
                _runtime.TryComplete(invocation.Runtime, invocation.OperationId, _ => Error(invocation, "NotFound")) : CompleteReceipt(invocation, receipt));
        }
        catch (Exception exception)
        {
            Log.Error($"META clinical receipt query {id} failed: {exception}");
            await OnMainThread(() => _runtime.TryComplete(invocation.Runtime, invocation.OperationId, _ => Error(invocation, "IoFailure")));
        }
        finally { _admissions.Enqueue(() => _statusReads--); }
    }

    private bool CompleteReceipt(NCMetaDeviceInvocation invocation, MetaOperationReceipt receipt) =>
        _runtime.CanContinue(invocation) && _runtime.TryComplete(invocation.Runtime, invocation.OperationId,
            process => process.CreateResult(ReturnType(invocation), ReceiptValue(process, receipt)));

    private MetaValue ReceiptValue(MetaProcess process, MetaOperationReceipt receipt)
    {
        var option = process.GetTypeId("option<meta.system.ErrorCode>");
        var error = string.IsNullOrEmpty(receipt.ErrorCode) ? process.CreateVariant(option, 0) :
            process.CreateVariant(option, 1, process.CreateEnum(process.GetTypeId("meta.system.ErrorCode"),
                Array.IndexOf(MetaApiCatalog.Default.Types.Single(type => type.Name == "meta.system.ErrorCode").Variants, receipt.ErrorCode)));
        return process.CreateRecord(process.GetTypeId("meta.medical.OperationReceipt"), [process.CreateString(receipt.Id),
            process.CreateEnum(process.GetTypeId("meta.medical.OperationStatus"), (int) receipt.Status), error, _runtime.CreateStamp(process)]);
    }
}
