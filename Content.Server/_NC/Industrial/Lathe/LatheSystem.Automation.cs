// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using Content.Server.Lathe.Components;
using Content.Server.Power.EntitySystems;
using Content.Shared.Lathe;
using Content.Shared.Research.Prototypes;
using Content.Shared.Materials;
using Content.Shared.Power;
using Robust.Shared.Prototypes;

namespace Content.Server.Lathe;

[RegisterComponent]
public sealed partial class NCLatheAutomationComponent : Component;

public sealed record NCLatheAutomationOutcome(EntityUid Device, Guid OperationId, int Completed, int Requested, bool Applied, string Error);

/// <summary>Exclusive native batches. Materials and physical output remain owned by the normal lathe.</summary>
public sealed partial class LatheSystem
{
    private sealed class AutomationJob(Guid id, LatheRecipePrototype recipe, int quantity, float materialMultiplier,
        float timeMultiplier, Dictionary<ProtoId<MaterialPrototype>, int> costs, Func<bool> guard)
    {
        public readonly Guid Id = id;
        public readonly LatheRecipePrototype Recipe = recipe;
        public readonly int Requested = quantity;
        public readonly float MaterialMultiplier = materialMultiplier;
        public readonly float TimeMultiplier = timeMultiplier;
        public readonly Dictionary<ProtoId<MaterialPrototype>, int> Costs = costs;
        public readonly Func<bool> Guard = guard;
        public int Completed;
        public bool Applying;
        public bool Reserved;
        public bool OutputCommitting;
    }
    private readonly Dictionary<EntityUid, AutomationJob> _automationJobs = [];
    private readonly Dictionary<EntityUid, AutomationJob> _automationOutputs = [];
    public event Action<NCLatheAutomationOutcome>? AutomationCompleted;

    private void InitializeAutomation() => SubscribeLocalEvent<NCLatheAutomationComponent, ComponentShutdown>(OnAutomationShutdown);
    private void OnAutomationShutdown(Entity<NCLatheAutomationComponent> ent, ref ComponentShutdown args)
    { TryCancelAutomationJob(ent.Owner, "DeviceDestroyed"); _automationOutputs.Remove(ent.Owner); }

    public bool CanStartAutomationJob(EntityUid uid, LatheRecipePrototype recipe, int quantity)
    {
        if (_automationJobs.Count >= 64 || _automationJobs.ContainsKey(uid) || _automationOutputs.ContainsKey(uid) || quantity is < 1 or > 16 ||
            !TryComp<LatheComponent>(uid, out var lathe) || lathe.CurrentRecipe != null || lathe.Queue.Count != 0 ||
            !this.IsPowered(uid, EntityManager) || !float.IsFinite(lathe.MaterialUseMultiplier) || lathe.MaterialUseMultiplier is <= 0 or > 4 ||
            !float.IsFinite(lathe.TimeMultiplier) || lathe.TimeMultiplier is <= 0 or > 10 ||
            recipe.CompleteTime.TotalSeconds * lathe.TimeMultiplier * quantity is < 0 or > 300 ||
            recipe.Result == null && recipe.ResultReagents == null || recipe.Materials.Count > 16 || !CanProduce(uid, recipe, quantity, lathe)) return false;
        return GetAdjustedAmount(lathe, recipe).All(cost => cost.amount is >= 0 and <= 1_000_000);
    }

    public bool TryStartAutomationJob(EntityUid uid, Guid operationId, LatheRecipePrototype recipe, int quantity, Func<bool> guard)
    {
        if (operationId == Guid.Empty || !guard() || !CanStartAutomationJob(uid, recipe, quantity) || !TryComp<LatheComponent>(uid, out var lathe)) return false;
        var job = new AutomationJob(operationId, recipe, quantity, lathe.MaterialUseMultiplier, lathe.TimeMultiplier,
            GetAdjustedAmount(lathe, recipe).ToDictionary(cost => cost.mat, cost => cost.amount), guard) { Applying = true };
        EnsureComp<NCLatheAutomationComponent>(uid);
        _automationJobs.Add(uid, job);
        try
        {
            // TryAddToQueue is the one native material debit. No adapter manufactures a reservation or output.
            if (!TryAddToQueue(uid, recipe, quantity, lathe)) { _automationJobs.Remove(uid); return false; }
            job.Reserved = true;
            if (!TryStartProducing(uid, lathe)) { TryCancelAutomationJob(uid, "Interlock"); return false; }
            return true;
        }
        catch
        {
            TryCancelAutomationJob(uid, "OutcomeUnknown"); throw;
        }
        finally { job.Applying = false; }
    }

    public bool TryCancelAutomationJob(EntityUid uid, string error = "Cancelled")
    {
        if (!_automationJobs.Remove(uid, out var job)) return false;
        var refundFailed = false;
        if (TryComp<LatheComponent>(uid, out var lathe))
        {
            // Refund the exact costs reserved at admission. Native Abort must not refund the current item twice.
            var remaining = Math.Max(0, job.Requested - job.Completed - (job.OutputCommitting ? 1 : 0));
            if (job.Reserved)
                foreach (var (material, cost) in job.Costs)
                    if (cost > 0 && remaining > 0 && !_materialStorage.TryChangeMaterialAmount(uid, material, cost * remaining)) refundFailed = true;
            lathe.Queue.Clear(); lathe.CurrentRecipe = null;
            AbortProduction(uid, lathe);
        }
        AutomationCompleted?.Invoke(new(uid, job.Id, job.Completed, job.Requested, false, refundFailed || job.OutputCommitting ? "OutcomeUnknown" : error));
        return true;
    }

    public bool TryCancelAutomationJob(EntityUid uid, Guid operationId, string error = "Cancelled") =>
        _automationJobs.TryGetValue(uid, out var job) && job.Id == operationId && TryCancelAutomationJob(uid, error);

    private void TakeAutomationManualControl(EntityUid uid)
    {
        if (_automationJobs.TryGetValue(uid, out var job) && !job.Applying) TryCancelAutomationJob(uid, "ControlLost");
    }

    private bool CanContinueAutomation(EntityUid uid, LatheComponent lathe)
    {
        if (!_automationJobs.TryGetValue(uid, out var job)) return true;
        if (!job.Guard() || !_automationJobs.TryGetValue(uid, out var currentJob) || currentJob != job ||
            !this.IsPowered(uid, EntityManager) || lathe.MaterialUseMultiplier != job.MaterialMultiplier ||
            lathe.TimeMultiplier != job.TimeMultiplier || lathe.CurrentRecipe is { } current && current != job.Recipe.ID ||
            lathe.Queue.Count > 1 || lathe.Queue.Any(batch => batch.Recipe != job.Recipe.ID))
        { TryCancelAutomationJob(uid, "PermissionRevoked"); return false; }
        return true;
    }

    private bool CanCompleteAutomationItem(EntityUid uid, LatheComponent lathe, LatheProducingComponent production)
    {
        if (!CanContinueAutomation(uid, lathe)) return false;
        if (!_automationJobs.TryGetValue(uid, out var job)) return true;
        if (job.OutputCommitting)
        { TryCancelAutomationJob(uid, "OutcomeUnknown"); _automationOutputs.Remove(uid); return false; }
        if (_timing.CurTime - production.StartTime < production.ProductionLength) return false;
        // Spawn/solution callbacks may synchronously cancel the machine. Its in-flight item cannot be refunded.
        job.OutputCommitting = lathe.CurrentRecipe != null;
        if (job.OutputCommitting) _automationOutputs[uid] = job;
        return true;
    }

    private bool OnAutomationItemProduced(EntityUid uid)
    {
        if (!_automationOutputs.Remove(uid, out var job)) return true;
        if (!_automationJobs.TryGetValue(uid, out var current) || current != job) return false;
        job.OutputCommitting = false;
        job.Completed++;
        if (job.Completed < job.Requested) return true;
        _automationJobs.Remove(uid);
        AutomationCompleted?.Invoke(new(uid, job.Id, job.Completed, job.Requested, true, string.Empty));
        return true;
    }
}
