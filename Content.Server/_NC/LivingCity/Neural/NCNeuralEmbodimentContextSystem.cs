// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Cognition;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Medical.Bleeding;
using Content.Shared._NC.Medical.Physiology;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.ActionBlocker;
using Content.Shared.Body.Components;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Systems;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Nutrition.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Neural;

/// <summary>
/// Projects authoritative physical state into stable semantic keys for one actor's neural request.
/// This is a read-only view of vanilla systems and never creates a parallel needs simulation.
/// </summary>
public sealed partial class NCNeuralEmbodimentContextSystem : EntitySystem
{
    [Dependency] private ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private DamageableSystem _damageable = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private NCNeuralActionExecutionSystem _execution = default!;
    [Dependency] private NCNeuralCommandResultRegistrySystem _commandResults = default!;
    [Dependency] private NCHumanActionRouterSystem _router = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;

    public NCNeuralBodyContext BuildBody(EntityUid actor)
    {
        TryComp<MobStateComponent>(actor, out var mobState);
        var lifeState = LifeStateKey(actor, mobState);
        var hungerState = TryComp<HungerComponent>(actor, out var hunger)
            ? HungerStateKey(hunger.CurrentThreshold)
            : "not-applicable";
        var thirstState = TryComp<ThirstComponent>(actor, out var thirst)
            ? ThirstStateKey(thirst.CurrentThirstThreshold)
            : "not-applicable";
        var mobilityState = lifeState switch
        {
            "dead" => "none",
            "critical" => "impaired",
            _ => _actionBlocker.CanMove(actor) ? "normal" : "blocked",
        };
        // NC bodies deliberately do not mirror wounds into vanilla Damageable HP. Prefer their authoritative
        // physiology and bleeding ledgers so the model cannot be told that a freshly wounded body feels fine.
        var painState = TryComp<NCPhysiologyComponent>(actor, out var physiology)
            ? PainStateKey(physiology.PainLevel)
            : TryComp<DamageableComponent>(actor, out var damageable) &&
              _damageable.GetTotalDamage((actor, damageable)) > 0
                ? lifeState == "critical" ? "severe" : "present"
                : "none";
        var bleeding = TryComp<NCBleedingComponent>(actor, out var ncBleeding)
            ? HasActiveBleeding(ncBleeding)
            : TryComp<BloodstreamComponent>(actor, out var bloodstream) && bloodstream.BleedAmount > 0f;

        return new NCNeuralBodyContext(
            lifeState,
            hungerState,
            thirstState,
            mobilityState,
            painState,
            bleeding);
    }

    public NCNeuralCurrentIntent? BuildCurrentIntent(NCActorId actorId)
    {
        if (!_execution.TryGetSnapshot(actorId, out var snapshot))
            return null;

        return new NCNeuralCurrentIntent(
            snapshot.CapabilityId,
            snapshot.CurrentStep,
            snapshot.StepCount,
            snapshot.ExpiresAt.Ticks)
        {
            IntentKey = snapshot.IntentKey,
            TargetHandle = snapshot.TargetHandle,
        };
    }

    public NCNeuralCommandResult[] BuildRecentCommandResults(NCActorId actorId)
    {
        NCNeuralCommandResult[] durable = _commandResults.TryGetCached(actorId, out var record)
            ? record.Snapshot.Results
            : [];
        var runtime = _execution.GetRecentCommandResults(actorId);
        NCNeuralCommandResult[] current = TryBuildRuntimeResult(actorId, out var result) ? [result] : [];
        return MergeRecentCommandResults(durable, runtime, current);
    }

    private bool TryBuildRuntimeResult(NCActorId actorId, out NCNeuralCommandResult result)
    {
        result = default!;
        if (!_router.TryGetSnapshot(actorId, out var snapshot) ||
            snapshot.State is NCHumanActionRuntimeState.Pending or NCHumanActionRuntimeState.Running ||
            snapshot.PlanId == Guid.Empty || string.IsNullOrWhiteSpace(snapshot.CapabilityId) ||
            snapshot.CommandIndex is < 0 or >= NCNeuralCommandResultRegistrySystem.MaximumResults)
        {
            return false;
        }

        var state = RuntimeStateKey(snapshot.State);
        if (snapshot.State == NCHumanActionRuntimeState.Completed &&
            _prototypes.TryIndex(new ProtoId<NCHumanActionPrototype>(snapshot.ActionId), out var action) &&
            action != null)
        {
            state = NCNeuralActionExecutionSystem.SuccessfulCommandState(action.Operation);
        }

        result = new NCNeuralCommandResult(
            snapshot.ActionId,
            state,
            FailureKindKey(snapshot.FailureKind),
            NormalizeFailure(snapshot.Failure),
            snapshot.UpdatedAt.Ticks)
        {
            RequestId = snapshot.PlanId,
            CapabilityId = snapshot.CapabilityId,
            CommandIndex = snapshot.CommandIndex,
        };
        return true;
    }

    /// <summary>Combines restart-safe outcomes with this process's latest acknowledgements without blocking on I/O.</summary>
    internal static NCNeuralCommandResult[] MergeRecentCommandResults(
        IEnumerable<NCNeuralCommandResult> durable,
        IEnumerable<NCNeuralCommandResult> runtime,
        IEnumerable<NCNeuralCommandResult> current)
    {
        var merged = new Dictionary<(Guid RequestId, int CommandIndex, string State), NCNeuralCommandResult>();
        foreach (var result in durable.Concat(runtime).Concat(current))
        {
            var key = (result.RequestId, result.CommandIndex, result.State);
            if (!merged.TryGetValue(key, out var previous) || result.UpdatedAtTicks >= previous.UpdatedAtTicks)
                merged[key] = result;
        }

        return merged.Values
            .OrderBy(result => result.UpdatedAtTicks)
            .ThenBy(result => result.RequestId)
            .ThenBy(result => result.CommandIndex)
            .ThenBy(result => result.State, StringComparer.Ordinal)
            .TakeLast(NCNeuralCommandResultRegistrySystem.MaximumResults)
            .ToArray();
    }

    private string LifeStateKey(EntityUid actor, MobStateComponent? state)
    {
        if (_mobState.IsAlive(actor, state))
            return "alive";
        if (_mobState.IsCritical(actor, state))
            return "critical";
        if (_mobState.IsDead(actor, state))
            return "dead";
        return "unknown";
    }

    private static string HungerStateKey(HungerThreshold state) => state switch
    {
        HungerThreshold.Overfed => "overfed",
        HungerThreshold.Okay => "satisfied",
        HungerThreshold.Peckish => "hungry",
        HungerThreshold.Starving => "starving",
        HungerThreshold.Dead => "fatal",
        _ => "unknown",
    };

    private static string ThirstStateKey(ThirstThreshold state) => state switch
    {
        ThirstThreshold.OverHydrated => "overhydrated",
        ThirstThreshold.Okay => "satisfied",
        ThirstThreshold.Thirsty => "thirsty",
        ThirstThreshold.Parched => "parched",
        ThirstThreshold.Dead => "fatal",
        _ => "unknown",
    };

    internal static string PainStateKey(NCPainLevel state) => state switch
    {
        NCPainLevel.None => "none",
        NCPainLevel.Mild => "mild",
        NCPainLevel.Moderate => "moderate",
        NCPainLevel.Severe => "severe",
        NCPainLevel.Excruciating => "excruciating",
        NCPainLevel.Overwhelming => "overwhelming",
        _ => "unknown",
    };

    internal static bool HasActiveBleeding(NCBleedingComponent bleeding) =>
        bleeding.ActiveExternalRate > 0f || bleeding.ActiveInternalRate > 0f ||
        bleeding.OverflowExternalRate > 0f || bleeding.OverflowInternalRate > 0f ||
        bleeding.Sources.Any(source => source.CurrentRate > 0f);

    private static string RuntimeStateKey(NCHumanActionRuntimeState state) => state switch
    {
        NCHumanActionRuntimeState.Completed => "completed",
        NCHumanActionRuntimeState.Failed => "failed",
        NCHumanActionRuntimeState.Interrupted => "interrupted",
        NCHumanActionRuntimeState.Pending => "pending",
        NCHumanActionRuntimeState.Running => "running",
        _ => "unknown",
    };

    private static string FailureKindKey(NCHumanActionFailureKind kind) => kind switch
    {
        NCHumanActionFailureKind.None => "none",
        NCHumanActionFailureKind.Configuration => "configuration",
        NCHumanActionFailureKind.RuntimeUnavailable => "runtime-unavailable",
        NCHumanActionFailureKind.InvalidRequest => "invalid-request",
        NCHumanActionFailureKind.CapabilityUnavailable => "capability-unavailable",
        NCHumanActionFailureKind.Blocked => "blocked",
        NCHumanActionFailureKind.Unreachable => "unreachable",
        NCHumanActionFailureKind.Busy => "busy",
        NCHumanActionFailureKind.TimedOut => "timed-out",
        NCHumanActionFailureKind.ExecutorUnhandled => "executor-unhandled",
        NCHumanActionFailureKind.DomainRejected => "domain-rejected",
        _ => "unknown",
    };

    private static string NormalizeFailure(string? failure)
    {
        if (string.IsNullOrWhiteSpace(failure))
            return string.Empty;

        var normalized = new string(failure.Trim().Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' ? character : '-').ToArray());
        return normalized[..Math.Min(normalized.Length, 128)];
    }
}
