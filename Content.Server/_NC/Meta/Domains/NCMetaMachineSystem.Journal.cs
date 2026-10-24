// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Server.Database;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Domains;

public sealed partial class NCMetaMachineSystem
{
    private const string ReceiptType = "meta.machine.operation";
    private static readonly JsonSerializerOptions ReceiptJson = new() { MaxDepth = 8 };

    private async Task<MetaMachineReceipt?> LoadAsync(Guid id, string owner, string device, string kind)
    {
        var snapshot = await _persistence.ReadSnapshotAsync(ReceiptType, id.ToString("D"));
        if (snapshot == null) return null;
        if (snapshot.Payload.Length > 8192 || snapshot.FormatVersion != 1) throw new InvalidDataException("Invalid machine receipt.");
        var receipt = JsonSerializer.Deserialize<MetaMachineReceipt>(snapshot.Payload, ReceiptJson);
        if (receipt == null || receipt.Operation == null || !receipt.IsValid || receipt.Operation.Id != id.ToString("N") ||
            receipt.Operation.Owner != owner || receipt.Operation.DeviceId != device || receipt.Operation.Kind != kind ||
            receipt.Operation.Revision != snapshot.AggregateVersion || receipt.Operation.Fingerprint.Length != 64)
            throw new InvalidDataException("Machine receipt scope mismatch.");
        return receipt;
    }
    private async Task<bool> SaveAsync(MetaMachineReceipt receipt, Guid id, NCSessionEpoch epoch, NCActiveWorldTime time)
    {
        if (!receipt.IsValid) throw new InvalidDataException("Invalid native output receipt.");
        var operation = receipt.Operation;
        var requestId = NCMetaPersistenceSystem.StableGuid("machine-receipt:" + operation.Id + ":" + operation.Revision);
        var payload = JsonSerializer.Serialize(receipt, ReceiptJson);
        var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(requestId,
            "meta.machine.transition", "meta.owner", operation.Owner, Guid.NewGuid(), "meta.machine.transitioned",
            ReceiptType, id.ToString("D"), operation.Revision - 1, 1, payload, "{}", payload, id, null, epoch, time, [],
            RequestFingerprint: MetaOperationReceipt.HashFields(operation.Fingerprint, operation.Revision.ToString(CultureInfo.InvariantCulture),
                operation.Status.ToString(), operation.ErrorCode, receipt.Completed.ToString(CultureInfo.InvariantCulture))));
        return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted;
    }
    private async Task<bool> SaveAsync(Job job, MetaMachineReceipt receipt)
    {
        var captured = await OnMain(() => _persistence.TryCaptureTime(out var epoch, out var time) && epoch == job.Epoch
            ? (epoch, time) : (job.Epoch, job.Time));
        return await SaveAsync(receipt, job.Id, captured.Item1, captured.Item2);
    }
    private async Task ExecuteAsync(Job job, IDisposable lease)
    {
        var invocation = job.Invocation;
        try
        {
            var previous = await LoadAsync(job.Id, invocation.Runtime.Owner, invocation.DeviceId, job.Kind);
            if (previous != null)
            {
                if (previous.Operation.Fingerprint != job.Fingerprint)
                { await OnMain(() => _runtime.TryComplete(invocation.Runtime, invocation.OperationId, _ => Error(invocation, "Conflict"))); return; }
                var recovered = previous.ReconcileInterrupted();
                if (recovered != previous && !await SaveAsync(job, recovered)) throw new IOException("Cannot reconcile machine operation.");
                await OnMain(() => Complete(invocation, recovered)); return;
            }
            var receipt = new MetaMachineReceipt(new(job.Id.ToString("N"), invocation.Runtime.Owner, invocation.DeviceId,
                job.Kind, job.Fingerprint, MetaOperationStatus.Prepared, "", 1), job.Requested, 0);
            if (!await SaveAsync(job, receipt)) throw new IOException("Cannot prepare machine operation.");
            receipt = receipt.Transition(MetaOperationStatus.Committing, 0);
            if (!await SaveAsync(job, receipt)) throw new IOException("Cannot persist material/output admission.");
            var outcome = await OnMain(() => ApplyNative(job));
            receipt = receipt.Transition(outcome.Status, outcome.Completed, outcome.Error);
            if (!await SaveAsync(job, receipt)) throw new IOException("Cannot persist native machine outcome.");
            await OnMain(() =>
            {
                var completed = Complete(invocation, receipt);
                if (receipt.Operation.Terminal)
                    _runtime.TryPublishEvent(invocation.Device, invocation.Member.Owner + ".operationChanged", process => ReceiptValue(process, receipt));
                return completed;
            });
            if (receipt.Operation.Status != MetaOperationStatus.Running) return;
            lease.Dispose();
            outcome = await job.Completion.Task;
            using var finalLease = _persistence.TryBeginNativeMutation();
            if (finalLease == null) return; // A restored Running receipt becomes OutcomeUnknown, never another native start.
            receipt = receipt.Transition(outcome.Status, outcome.Completed, outcome.Error);
            if (!await SaveAsync(job, receipt)) throw new IOException("Cannot settle native machine operation.");
            await OnMain(() => _runtime.TryPublishEvent(invocation.Device, invocation.Member.Owner + ".operationChanged", process => ReceiptValue(process, receipt)));
        }
        catch (Exception error)
        {
            Log.Error($"META machine operation {job.Id} requires reconciliation: {error}");
            await OnMain(() =>
            {
                CancelNative(job, "OutcomeUnknown");
                return _runtime.TryComplete(invocation.Runtime, invocation.OperationId, _ => Error(invocation, "OutcomeUnknown"));
            });
        }
        finally { lease.Dispose(); _callbacks.Enqueue(() => _jobs.Remove(job.Id)); }
    }
    private async Task ReadStatusAsync(NCMetaDeviceInvocation invocation, Guid id, string kind)
    {
        try
        {
            var receipt = await LoadAsync(id, invocation.Runtime.Owner, invocation.DeviceId, kind);
            var orphaned = await OnMain(() => _runtime.CanContinue(invocation) && receipt != null && !receipt.Operation.Terminal && !_jobs.ContainsKey(id));
            if (orphaned)
            {
                using var lease = _persistence.TryBeginNativeMutation();
                var capture = await OnMain(() => _persistence.TryCaptureTime(out var epoch, out var time) ? (epoch, time) : default);
                if (lease == null || capture == default) throw new IOException("Machine reconciliation is quiescing.");
                receipt = receipt!.ReconcileInterrupted();
                if (!await SaveAsync(receipt, id, capture.Item1, capture.Item2)) throw new IOException("Cannot reconcile orphaned machine receipt.");
            }
            await OnMain(() => receipt == null ? _runtime.TryComplete(invocation.Runtime, invocation.OperationId, _ => Error(invocation, "NotFound")) : Complete(invocation, receipt));
        }
        catch (Exception error)
        {
            Log.Error($"META machine receipt {id} query failed: {error}");
            await OnMain(() => _runtime.TryComplete(invocation.Runtime, invocation.OperationId, _ => Error(invocation, "IoFailure")));
        }
        finally { _callbacks.Enqueue(() => _statusReads--); }
    }
    private bool Complete(NCMetaDeviceInvocation invocation, MetaMachineReceipt receipt) => _runtime.CanContinue(invocation) &&
        _runtime.TryComplete(invocation.Runtime, invocation.OperationId, process => process.CreateResult(ReturnType(invocation), ReceiptValue(process, receipt)));
    private MetaValue ReceiptValue(MetaProcess process, MetaMachineReceipt receipt)
    {
        var code = receipt.Operation.ErrorCode;
        var option = process.GetTypeId("option<meta.system.ErrorCode>");
        var tag = Array.IndexOf(MetaApiCatalog.Default.Types.Single(type => type.Name == "meta.system.ErrorCode").Variants, code);
        if (tag < 0 && code.Length != 0) tag = Array.IndexOf(MetaApiCatalog.Default.Types.Single(type => type.Name == "meta.system.ErrorCode").Variants, "OutcomeUnknown");
        var error = code.Length == 0 ? process.CreateVariant(option, 0) : process.CreateVariant(option, 1, process.CreateEnum(process.GetTypeId("meta.system.ErrorCode"), tag));
        return process.CreateRecord(process.GetTypeId("meta.system.JobReceipt"), [process.CreateString(receipt.Operation.Id),
            process.CreateEnum(process.GetTypeId("meta.system.OperationStatus"), (int) receipt.Operation.Status), MetaValue.Int(receipt.Requested),
            MetaValue.Int(receipt.Completed), error, _runtime.CreateStamp(process)]);
    }
}
