// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Social;

namespace Content.Shared._NC.LivingCity.Neural;

/// <summary>Semantic body state available to the character without metagame entity data.</summary>
public sealed record NCNeuralBodyContext(
    string LifeState,
    string HungerState,
    string ThirstState,
    string MobilityState,
    string PainState,
    bool Bleeding);

/// <summary>The command sequence currently owning this physical body.</summary>
public sealed record NCNeuralCurrentIntent(
    string CapabilityId,
    int CurrentStep,
    int StepCount,
    long ExpiresAtTicks)
{
    /// <summary>The character's semantic goal, retained while its physical plan is still active.</summary>
    public string IntentKey { get; init; } = string.Empty;

    /// <summary>Request-scoped target selected for that goal, when one remains observable.</summary>
    public string TargetHandle { get; init; } = string.Empty;
}

/// <summary>Observed outcome of a command previously issued by this same ActorId.</summary>
public sealed record NCNeuralCommandResult(
    string ActionId,
    string State,
    string FailureKind,
    string FailureReason,
    long UpdatedAtTicks)
{
    /// <summary>The provider request that authored the command batch.</summary>
    public Guid RequestId { get; init; }

    /// <summary>The server capability selected by the provider command.</summary>
    public string CapabilityId { get; init; } = string.Empty;

    public int CommandIndex { get; init; }
    public string GoalKey { get; init; } = string.Empty;
    public string ExpectedOutcomeKey { get; init; } = string.Empty;
}

/// <summary>
/// A current perceived handle or server-known semantic location and the uses authoritative context exposes for it.
/// </summary>
public sealed record NCNeuralWorldOpportunity(
    string TargetHandle,
    IReadOnlyList<string> AffordanceKeys,
    bool Available,
    string UnavailableReason)
{
    /// <summary>Stable identity used to deduplicate and retire this opportunity across neural requests.</summary>
    public string OpportunityId { get; init; } = string.Empty;

    /// <summary>Server-authored semantic classification, such as event.assault or task.report-to-workplace.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Either subject or location; the bus enforces the matching actor-scoped visibility set.</summary>
    public string TargetKind { get; init; } = string.Empty;

    /// <summary>Capabilities that must all be present in this actor's current allowlist.</summary>
    public IReadOnlyList<string> RequiredCapabilityIds { get; init; } = [];

    public int Reward { get; init; }
    public int Urgency { get; init; }
    public int Risk { get; init; }
    public long CreatedAtTicks { get; init; }
    public long ExpiresAtTicks { get; init; }
    public string SourceKey { get; init; } = string.Empty;
    public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>Server-authored semantics for one capability the provider may select in this request.</summary>
public sealed record NCNeuralAvailableAction(
    string CapabilityId,
    string DomainKey,
    string TaskKindKey,
    IReadOnlyList<string> GoalKeys,
    IReadOnlyList<string> ActionIds,
    IReadOnlyList<string> OperationKeys,
    IReadOnlyList<string> TargetKinds);

/// <summary>A bounded request for another server-authoritative observation before committing to an action.</summary>
public sealed record NCNeuralSenseRequest(string Kind, string TargetHandle);

/// <summary>
/// Read-only outcome of one active-perception request. Every observed handle must also occur in the refreshed
/// request sensorium; a failed targeted inspection may retain only its previous opaque handle for correlation.
/// </summary>
public sealed record NCNeuralSenseResult(
    string Kind,
    string TargetHandle,
    string Status,
    IReadOnlyList<string> ObservedHandles,
    NCNeuralBodyContext? Body)
{
    /// <summary>Fact handles selected from this request's already-authorized memory packet.</summary>
    public IReadOnlyList<string> FactHandles { get; init; } = [];

    /// <summary>Semantic location handles selected from this request's already-known locations.</summary>
    public IReadOnlyList<string> LocationHandles { get; init; } = [];

    /// <summary>Targets selected from this request's already-visible world opportunities.</summary>
    public IReadOnlyList<string> OpportunityTargetHandles { get; init; } = [];

    /// <summary>Bounded semantic details derived only from the selected request entries.</summary>
    public IReadOnlyList<string> DetailKeys { get; init; } = [];
}

/// <summary>One optional line of physical speech selected by the same actor decision.</summary>
public sealed record NCNeuralSpeechAct(string ActKey, string Text);

/// <summary>A concise provider-selected intent. Physical commands remain independently validated.</summary>
public sealed record NCNeuralIntentChoice(string Key, string TargetHandle, int Priority);

/// <summary>The provider may request a calm follow-up; server event wakes always remain authoritative.</summary>
public sealed record NCNeuralWakeDirective(IReadOnlyList<string> On, int AfterSeconds);

/// <summary>Untrusted candidate for later evidence-backed memory consolidation.</summary>
public sealed record NCNeuralMemorySuggestion(string Key, string SubjectHandle, int Importance);

/// <summary>Requests a second bounded perception pass without permitting an unbounded tool loop.</summary>
public sealed class NCNeuralSenseRequestedEvent(
    NCNeuralRequestEnvelope request,
    IReadOnlyList<NCNeuralSenseRequest> requests,
    NCConversationSessionId? conversationSessionId = null) : EntityEventArgs
{
    public NCNeuralRequestEnvelope Request { get; } = request;
    public IReadOnlyList<NCNeuralSenseRequest> Requests { get; } = requests;
    public NCConversationSessionId? ConversationSessionId { get; } = conversationSessionId;
}

/// <summary>Asks the physical brain scheduler for a provider-selected follow-up time.</summary>
public sealed class NCNeuralWakeScheduledEvent(
    NCNeuralRequestEnvelope request,
    IReadOnlyList<string> on,
    int afterSeconds) : EntityEventArgs
{
    public NCNeuralRequestEnvelope Request { get; } = request;
    public IReadOnlyList<string> On { get; } = on;
    public int AfterSeconds { get; } = afterSeconds;
}
