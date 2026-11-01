// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server.Chemistry.Components;
using Content.Server.Chemistry.EntitySystems;
using Content.Server._NC.Meta.Runtime;
using Content.Meta.Language;
using Content.Shared.Chemistry;
using Content.Shared.Kitchen;
using Content.Shared.Kitchen.Components;
using Content.Shared.Kitchen.EntitySystems;

namespace Content.Server._NC.Meta.Domains;

public sealed partial class NCMetaMachineSystem
{
    [Dependency] private SharedReagentGrinderSystem _grinders = default!;
    [Dependency] private ChemMasterSystem _chemMasters = default!;
    private void InitializeLaboratory() => _grinders.AutomationCompleted += OnGrinderCompleted;
    private void ShutdownLaboratory() => _grinders.AutomationCompleted -= OnGrinderCompleted;
    private bool HasGrinder(EntityUid uid) => HasComp<ReagentGrinderComponent>(uid);
    private bool HasChemMaster(EntityUid uid) => HasComp<ChemMasterComponent>(uid);
    private bool IsLaboratoryBusy(EntityUid uid) => HasGrinder(uid) && _grinders.IsActive(uid);
    private void OnGrinderCompleted(NCGrinderAutomationOutcome result)
    {
        if (_jobs.TryGetValue(result.OperationId, out var job) && job.Kind == "grinder" && job.Invocation.Device == result.Device)
            job.Completion.TrySetResult(new(result.Applied ? MetaOperationStatus.Applied : result.Error == "OutcomeUnknown" ? MetaOperationStatus.OutcomeUnknown : MetaOperationStatus.Rejected,
                result.Applied ? 1 : 0, result.Error));
    }
    private bool TryLaboratoryArguments(NCMetaDeviceInvocation invocation, string kind, out int requested, out int program, out int amount, out string label)
    {
        requested = 1; program = 0; amount = 0; label = "";
        var process = invocation.Runtime.Process; var arguments = invocation.Request.Arguments;
        if (kind == "grinder") { program = process.GetVariantTag(arguments[0]); return program is 0 or 1; }
        var count = invocation.Member.Name == "pills" ? arguments[0].AsInt() : 1;
        var dose = arguments[invocation.Member.Name == "pills" ? 1 : 0].AsInt();
        label = process.GetString(arguments[^2]);
        if (count is < 1 or > 16 || dose is < 1 or > 1000 || label.Length > SharedChemMaster.LabelMaxLength) return false;
        requested = (int) count; amount = (int) dose; return true;
    }
    private Outcome ApplyLaboratory(Job job)
    {
        if (job.Kind == "grinder")
        {
            if (!_grinders.TryStartAutomationJob(job.Invocation.Device, job.Id, (GrinderProgram) job.Program, () => CanCommit(job)))
                return new(MetaOperationStatus.Rejected, 0, "Interlock");
            job.Started = true; return new(MetaOperationStatus.Running, 0);
        }
        if (!TryComp<NCMetaMachineComponent>(job.Invocation.Device, out var machine) || machine.Operator is not { } actor ||
            !_devices.CanManage(job.Invocation.Device, actor) || !_blocker.CanConsciouslyPerformAction(actor) ||
            job.Invocation.Actor is { } requestActor && requestActor != actor) return new(MetaOperationStatus.Rejected, 0, "AccessDenied");
        var result = job.Invocation.Member.Name == "pills"
            ? _chemMasters.TryCreatePills(job.Invocation.Device, actor, job.Requested, job.Amount, job.Label, () => CanCommit(job))
            : _chemMasters.TryBottle(job.Invocation.Device, actor, job.Amount, job.Label, () => CanCommit(job));
        return !result.Consumed ? new(MetaOperationStatus.Rejected, 0, "ActionRejected") :
            result.Completed == job.Requested ? new(MetaOperationStatus.Applied, result.Completed) :
            new(MetaOperationStatus.OutcomeUnknown, result.Completed, "OutcomeUnknown");
    }
    private void CancelLaboratory(Job job, string error)
    {
        if (job.Kind == "grinder") _grinders.TryCancelAutomationJob(job.Invocation.Device, job.Id, error);
    }
}
