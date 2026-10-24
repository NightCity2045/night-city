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

namespace Content.Server._NC.Meta.Domains;

public sealed partial class NCMetaDroneOperationsSystem
{
    private static readonly JsonSerializerOptions ReceiptJson = new() { MaxDepth = 8 };
    private async Task<MetaMachineReceipt?> LoadAsync(Guid id, NCMetaDeviceInvocation invocation, string kind)
    {
        var snapshot = await _persistence.ReadSnapshotAsync("meta.drone.operation", id.ToString("D"));
        if (snapshot == null) return null;
        if (snapshot.FormatVersion != 1 || snapshot.Payload.Length > 8192) throw new InvalidDataException("Invalid drone receipt.");
        var receipt = JsonSerializer.Deserialize<MetaMachineReceipt>(snapshot.Payload, ReceiptJson);
        if (receipt == null || !receipt.IsValid || receipt.Requested != 1 || receipt.Operation.Owner != invocation.Runtime.Owner ||
            receipt.Operation.DeviceId != invocation.DeviceId || receipt.Operation.Kind != kind || receipt.Operation.Id != id.ToString("N") ||
            receipt.Operation.Revision != snapshot.AggregateVersion || receipt.Operation.Fingerprint.Length != 64) throw new InvalidDataException("Drone receipt scope mismatch.");
        return receipt;
    }
    private async Task<bool> SaveAsync(Job job, MetaMachineReceipt receipt)
    {
        if (!receipt.IsValid) throw new InvalidDataException("Invalid native drone output receipt.");
        var operation = receipt.Operation; var payload = JsonSerializer.Serialize(receipt, ReceiptJson);
        var time = await OnMain(() => _persistence.TryCaptureTime(out var epoch, out var current) && epoch == job.Epoch ? current : job.Time);
        var result = await _database.ApplyNCPersistentMutationAsync(new NCPersistentMutationRequest(
            NCMetaPersistenceSystem.StableGuid("drone-receipt:" + operation.Id + ":" + operation.Revision), "meta.drone.transition", "meta.owner", operation.Owner,
            Guid.NewGuid(), "meta.drone.transitioned", "meta.drone.operation", job.Id.ToString("D"), operation.Revision - 1, 1, payload, "{}", payload,
            job.Id, null, job.Epoch, time, [], RequestFingerprint: MetaOperationReceipt.HashFields(operation.Fingerprint,
                operation.Revision.ToString(CultureInfo.InvariantCulture), operation.Status.ToString(), operation.ErrorCode, receipt.Completed.ToString(CultureInfo.InvariantCulture))));
        return result.Status is NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted;
    }
    private async Task ExecuteAsync(Job job, IDisposable lease)
    {
        try
        {
            var previous = await LoadAsync(job.Id, job.Invocation, job.Kind);
            if (previous != null)
            {
                if (previous.Operation.Fingerprint != job.Fingerprint)
                { await OnMain(() => _runtime.TryComplete(job.Invocation.Runtime, job.Invocation.OperationId, _ => Error(job.Invocation, "Conflict"))); return; }
                var recovered = previous.ReconcileInterrupted();
                if (previous != recovered && !await SaveAsync(job, recovered)) throw new IOException("Cannot reconcile drone receipt.");
                await OnMain(() => Complete(job.Invocation, recovered)); return;
            }
            var receipt = new MetaMachineReceipt(new(job.Id.ToString("N"), job.Invocation.Runtime.Owner, job.Invocation.DeviceId, job.Kind,
                job.Fingerprint, MetaOperationStatus.Prepared, "", 1), 1, 0);
            if (!await SaveAsync(job, receipt)) throw new IOException("Cannot prepare drone operation.");
            receipt = receipt.Transition(MetaOperationStatus.Committing, 0);
            if (!await SaveAsync(job, receipt)) throw new IOException("Cannot admit drone output.");
            await OnMain(() => { BeginNative(job); return true; });
            var outcome = await job.Completion.Task;
            receipt = receipt.Transition(outcome.Status, outcome.Completed, outcome.Error);
            if (!await SaveAsync(job, receipt)) throw new IOException("Cannot settle drone output.");
            await OnMain(() =>
            {
                Complete(job.Invocation, receipt);
                return _runtime.TryPublishEvent(job.Invocation.Device, job.Invocation.Member.Owner + ".operationChanged", process => ReceiptValue(process, receipt));
            });
        }
        catch (Exception error)
        {
            Log.Error($"META drone operation {job.Id} requires reconciliation: {error}");
            await OnMain(() => { Cancel(job, "OutcomeUnknown"); return _runtime.TryComplete(job.Invocation.Runtime, job.Invocation.OperationId, _ => Error(job.Invocation, "OutcomeUnknown")); });
        }
        finally
        {
            lease.Dispose();
            _callbacks.Enqueue(() =>
            {
                if (_jobs.Remove(job.Id) && TryComp<NCMetaDroneComponent>(job.Invocation.Device, out var drone)) drone.PendingPhysicalOperations = Math.Max(0, drone.PendingPhysicalOperations - 1);
            });
        }
    }
    private async Task ReadStatusAsync(NCMetaDeviceInvocation invocation, Guid id, string kind)
    {
        try
        {
            var receipt = await LoadAsync(id, invocation, kind);
            // A new invocation may inspect an interrupted output, but it never restarts it.
            var live = await OnMain(() => _jobs.ContainsKey(id));
            if (receipt != null && !live && !receipt.Operation.Terminal)
            {
                using var lease = _persistence.TryBeginNativeMutation();
                var captured = await OnMain(() => _runtime.CanContinue(invocation) && _persistence.TryCaptureTime(out var epoch, out var time) ? (epoch, time) : default);
                if (lease == null || captured == default) throw new IOException("Drone reconciliation is quiescing.");
                receipt = receipt.ReconcileInterrupted();
                var recovery = new Job(invocation, id, kind, receipt.Operation.Fingerprint, default, null, 0, captured.Item1, captured.Item2);
                if (!await SaveAsync(recovery, receipt)) throw new IOException("Cannot settle interrupted drone receipt.");
            }
            await OnMain(() => receipt == null ? _runtime.TryComplete(invocation.Runtime, invocation.OperationId, _ => Error(invocation, "NotFound")) : Complete(invocation, receipt));
        }
        catch (Exception error)
        {
            Log.Error($"META drone receipt {id} query failed: {error}");
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
            process.CreateEnum(process.GetTypeId("meta.system.OperationStatus"), (int) receipt.Operation.Status), MetaValue.Int(1), MetaValue.Int(receipt.Completed), error, _runtime.CreateStamp(process)]);
    }
}
