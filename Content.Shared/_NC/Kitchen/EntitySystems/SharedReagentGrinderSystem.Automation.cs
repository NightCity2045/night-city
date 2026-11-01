// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using Content.Shared.FixedPoint;
using Content.Shared.Jittering;
using Content.Shared.Kitchen.Components;
using Content.Shared.Power;

namespace Content.Shared.Kitchen.EntitySystems;

[RegisterComponent]
public sealed partial class NCGrinderAutomationComponent : Component;
public sealed record NCGrinderAutomationOutcome(EntityUid Device, Guid OperationId, int Completed, int Requested, bool Applied, string Error);

public abstract partial class SharedReagentGrinderSystem
{
    private sealed class AutomationJob(Guid id, EntityUid beaker, EntityUid[] inputs, int units, Func<bool> guard)
    {
        public readonly Guid Id = id;
        public readonly EntityUid Beaker = beaker;
        public readonly EntityUid[] Inputs = inputs;
        public readonly int Requested = units;
        public readonly Func<bool> Guard = guard;
        public int Completed;
        public bool Committing;
    }
    private readonly Dictionary<EntityUid, AutomationJob> _automationJobs = [];
    private readonly HashSet<EntityUid> _automationOutputs = [];
    public event Action<NCGrinderAutomationOutcome>? AutomationCompleted;
    private void InitializeAutomation()
    {
        SubscribeLocalEvent<NCGrinderAutomationComponent, ComponentShutdown>(OnAutomationShutdown);
        SubscribeLocalEvent<NCGrinderAutomationComponent, PowerChangedEvent>(OnAutomationPower);
    }
    private void OnAutomationShutdown(Entity<NCGrinderAutomationComponent> ent, ref ComponentShutdown args)
    { TryCancelAutomationJob(ent.Owner, "DeviceDestroyed"); _automationOutputs.Remove(ent.Owner); }
    private void OnAutomationPower(Entity<NCGrinderAutomationComponent> ent, ref PowerChangedEvent args)
    { if (!args.Powered) TryCancelAutomationJob(ent.Owner, "PowerOff"); }

    public bool CanStartAutomationJob(EntityUid uid, GrinderProgram program)
    {
        if (!_net.IsServer || !Enum.IsDefined(program) || _automationJobs.Count >= 64 || _automationJobs.ContainsKey(uid) || _automationOutputs.Contains(uid) ||
            !TryComp<ReagentGrinderComponent>(uid, out var grinder) || IsActive((uid, grinder)) || grinder.AutoMode != GrinderAutoMode.Off ||
            !_power.IsPowered(uid) || grinder.InputContainer.ContainedEntities.Count is < 1 or > 16 ||
            !float.IsFinite(grinder.WorkTimeMultiplier) || grinder.WorkTimeMultiplier <= 0 ||
            grinder.WorkTime.TotalSeconds * grinder.WorkTimeMultiplier is <= 0 or > 300 ||
            _itemSlotsSystem.GetItemOrNull(uid, ReagentGrinderComponent.BeakerSlotId) is not { } beaker ||
            !_solutionContainersSystem.TryGetFitsInDispenser(beaker, out _, out var destination)) return false;
        var units = 0; var volume = FixedPoint2.Zero;
        foreach (var item in grinder.InputContainer.ContainedEntities)
        {
            var solution = GetGrinderSolution(item, program);
            var count = _stackSystem.GetCount(item);
            if (count is < 1 or > 16 || solution == null || solution.Volume <= 0 || solution.Volume > 1_000_000) return false;
            units += count; if (units > 16) return false;
            volume += solution.Volume * count;
        }
        return volume <= destination.AvailableVolume;
    }
    public bool TryStartAutomationJob(EntityUid uid, Guid id, GrinderProgram program, Func<bool> guard)
    {
        if (id == Guid.Empty || !guard() || !CanStartAutomationJob(uid, program) || !TryComp<ReagentGrinderComponent>(uid, out var grinder) ||
            _itemSlotsSystem.GetItemOrNull(uid, ReagentGrinderComponent.BeakerSlotId) is not { } beaker) return false;
        var inputs = grinder.InputContainer.ContainedEntities.ToArray();
        EnsureComp<NCGrinderAutomationComponent>(uid);
        _automationJobs.Add(uid, new(id, beaker, inputs, inputs.Sum(item => _stackSystem.GetCount(item)), guard));
        StartGrinder((uid, grinder), program);
        if (grinder.Program != program || grinder.EndTime == null) { _automationJobs.Remove(uid); return false; }
        return true;
    }
    public bool TryCancelAutomationJob(EntityUid uid, Guid id, string error = "Cancelled") =>
        _automationJobs.TryGetValue(uid, out var job) && job.Id == id && TryCancelAutomationJob(uid, error);
    private bool TryCancelAutomationJob(EntityUid uid, string error)
    {
        if (!_automationJobs.Remove(uid, out var job)) return false;
        if (TryComp<ReagentGrinderComponent>(uid, out var grinder))
        {
            grinder.Program = null; grinder.EndTime = null;
            grinder.AudioStream = _audioSystem.Stop(grinder.AudioStream); Dirty(uid, grinder);
            RemCompDeferred<ActiveReagentGrinderComponent>(uid); RemCompDeferred<JitteringComponent>(uid);
            _powerState.TrySetWorkingState(uid, false); UpdateUi(uid);
        }
        AutomationCompleted?.Invoke(new(uid, job.Id, job.Completed, job.Requested, false, job.Committing ? "OutcomeUnknown" : error));
        return true;
    }
    private void TakeAutomationManualControl(EntityUid uid) => TryCancelAutomationJob(uid, "ControlLost");
    private bool CanCompleteAutomation(Entity<ReagentGrinderComponent> ent)
    {
        if (!_automationJobs.TryGetValue(ent.Owner, out var job)) return true;
        if (job.Committing || !job.Guard() || !_automationJobs.TryGetValue(ent.Owner, out var current) || current != job || !_power.IsPowered(ent.Owner) ||
            _itemSlotsSystem.GetItemOrNull(ent.Owner, ReagentGrinderComponent.BeakerSlotId) != job.Beaker ||
            !job.Inputs.ToHashSet().SetEquals(ent.Comp.InputContainer.ContainedEntities))
        { TryCancelAutomationJob(ent.Owner, "PermissionRevoked"); _automationOutputs.Remove(ent.Owner); return false; }
        job.Committing = true; _automationOutputs.Add(ent.Owner); return true;
    }
    private Guid? AutomationOutput(EntityUid uid) => _automationJobs.TryGetValue(uid, out var job) ? job.Id : null;
    private bool CanContinueAutomationOutput(EntityUid uid, Guid? id)
    {
        if (id == null) return true;
        if (!_automationJobs.TryGetValue(uid, out var job) || job.Id != id) return false;
        if (!job.Guard() || !_power.IsPowered(uid) || _itemSlotsSystem.GetItemOrNull(uid, ReagentGrinderComponent.BeakerSlotId) != job.Beaker)
        { TryCancelAutomationJob(uid, "PermissionRevoked"); return false; }
        return true;
    }
    private void OnAutomationItemProduced(EntityUid uid, Guid? id, int units)
    { if (_automationJobs.TryGetValue(uid, out var job) && job.Id == id) job.Completed += units; }
    private void CompleteAutomation(EntityUid uid, Guid? id)
    {
        if (id == null) return;
        _automationOutputs.Remove(uid);
        if (!_automationJobs.TryGetValue(uid, out var job) || job.Id != id) return;
        _automationJobs.Remove(uid);
        AutomationCompleted?.Invoke(new(uid, job.Id, job.Completed, job.Requested, job.Completed == job.Requested,
            job.Completed == job.Requested ? "" : "Interlock"));
    }
}
