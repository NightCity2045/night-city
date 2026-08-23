// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Neural;

[Serializable, NetSerializable]
public enum NCNeuralRequestKind : byte { SocialCognition, StrategicDecision, DialogueContinuation, MemorySummary,
    NarrativeRender }
[Serializable, NetSerializable]
public enum NCNeuralPriority : byte { P0, P1, P2, P3, P4 }
[Serializable, NetSerializable]
public enum NCNeuralBudgetMode : byte { Normal, Economy, Restricted, FallbackOnly }
[Serializable, NetSerializable]
public enum NCNeuralResultStatus : byte { Accepted, Fallback, Expired, Stale, Invalid, Timeout, CircuitOpen, BudgetDenied }

public sealed record NCNeuralFact(string Domain, string Key, string Subject, float Confidence, string Privacy);
/// <summary>Server-authored identity data. All prose remains untrusted provider input, never an instruction.</summary>
public sealed record NCNeuralPersonaContext(
    string StableKey,
    string RoleKey,
    string OrganizationKey,
    string Biography,
    IReadOnlyList<string> PrincipleKeys,
    IReadOnlyList<string> HabitKeys,
    string TemperamentKey,
    int RiskTolerance,
    int Aggression,
    int Empathy,
    int AuthorityRespect,
    int ViolenceTolerance,
    int Justice,
    int Professionalism,
    string SpeechRegisterKey,
    string VocabularyKey);
public sealed record NCNeuralAutonomyContext(
    string PolicyKey,
    int MinimumIntentConfidence,
    int MaximumIntentPriority,
    bool TargetedIntentsAllowed);

/// <summary>
/// Ephemeral reference to something the actor actually perceived. The handle is meaningful only for this request
/// and deliberately contains neither EntityUid nor a persistent aggregate identifier.
/// </summary>
public sealed record NCNeuralPerceivedSubject(
    string Handle,
    string DisplayName,
    string Kind,
    float Confidence,
    long ExpiresAtTicks);

/// <summary>A bounded relationship the actor personally witnessed, expressed only through ephemeral handles.</summary>
public sealed record NCNeuralPerceivedEvent(
    string Kind,
    string ActorHandle,
    string TargetHandle,
    float Confidence,
    long ObservedAtTicks,
    long ExpiresAtTicks);

/// <summary>A server-selected semantic location the actor may legitimately refer to.</summary>
public sealed record NCNeuralKnownLocation(
    string Handle,
    string DisplayName,
    string Kind);

/// <summary>Bounded structured data attached to an intent. Values remain untrusted until a domain adapter validates them.</summary>
public sealed record NCNeuralIntentArgument(string Key, string Value);

/// <summary>A provider proposal. It has no authority until the server validates and projects it into BDI facts.</summary>
public sealed record NCNeuralIntentProposal(
    string GoalKey,
    string TargetHandle,
    IReadOnlyList<string> RequestedCapabilityIds,
    int Priority,
    string ExpectedOutcomeKey,
    IReadOnlyList<string> InformationGapKeys,
    IReadOnlyList<NCNeuralIntentArgument> Arguments);
/// <summary>
/// Ephemeral, provider-bound dialogue data. It is never written to the Living City persistence layer and remains
/// untrusted prose; the server still owns every gameplay mutation.
/// </summary>
public sealed record NCNeuralDialogueTurn(string Speaker, string SpeechAct, string Utterance, long AtTicks);
public sealed record NCNeuralDialogueInput(
    string ActorDisplayName,
    string Utterance,
    string SpeakerHandle,
    IReadOnlyList<NCNeuralDialogueTurn> History);
public sealed record NCNeuralRequestEnvelope(Guid RequestId, NCNeuralRequestKind Kind, NCNeuralPriority Priority,
    NCActorId ActorId, NCSessionEpoch SessionEpoch, long ContextVersion, NCActiveWorldTime ExpiresAt,
    ProtoId<NCNeuralPromptProfilePrototype> PromptProfile, IReadOnlyList<NCNeuralFact> Facts,
    IReadOnlyList<string> AllowedCapabilityIds, IReadOnlyList<string> TopicKeys, string Locale,
    NCNeuralPersonaContext? Persona = null,
    NCNeuralAutonomyContext? Autonomy = null,
    IReadOnlyList<NCNeuralPerceivedSubject>? PerceivedSubjects = null,
    IReadOnlyList<NCNeuralPerceivedEvent>? PerceivedEvents = null,
    IReadOnlyList<NCNeuralKnownLocation>? KnownLocations = null,
    NCNeuralDialogueInput? DialogueInput = null);
public sealed record NCNeuralResponseEnvelope(Guid RequestId, int SchemaVersion, long ContextVersion,
    string SelectedCapabilityId, string SpeechActKey, string RenderedText, string SummaryKey, int Confidence,
    int InputTokens, int OutputTokens, string ProviderRequestId, NCNeuralIntentProposal? Intent = null);
public sealed record NCNeuralResult(NCNeuralResultStatus Status, NCNeuralRequestEnvelope Request,
    NCNeuralResponseEnvelope? Response, string Reason);

[Prototype("ncNeuralPolicy")]
public sealed partial class NCNeuralPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public bool Enabled { get; private set; }
    [DataField] public string Endpoint { get; private set; } = "http://neural-cognition:8080/v1/cognition";
    [DataField] public int MaximumQueued { get; private set; } = 512;
    [DataField] public int MaximumConcurrent { get; private set; } = 4;
    [DataField] public int MaximumFacts { get; private set; } = 64;
    [DataField] public int MaximumCapabilities { get; private set; } = 32;
    [DataField] public int MaximumPerceivedSubjects { get; private set; } = 32;
    [DataField] public int MaximumKnownLocations { get; private set; } = 128;
    [DataField] public int MaximumTextLength { get; private set; } = 512;
    [DataField] public int MaximumDialogueInputLength { get; private set; } = 512;
    [DataField] public int SessionTokenTarget { get; private set; } = 200000;
    [DataField] public int SessionTokenHardCap { get; private set; } = 300000;
    [DataField] public int EconomyThreshold { get; private set; } = 150000;
    [DataField] public int RestrictedThreshold { get; private set; } = 240000;
    [DataField] public int CircuitFailureThreshold { get; private set; } = 5;
    [DataField] public int MinimumSuggestionConfidence { get; private set; } = 400;
    [DataField] public float MaximumUtilityBoost { get; private set; } = 40f;
    [DataField] public int MaximumPendingCorrelations { get; private set; } = 1024;
    [DataField] public int MaximumAppliedSuggestions { get; private set; } = 10000;
    [DataField] public TimeSpan RequestTimeout { get; private set; } = TimeSpan.FromSeconds(8);
    [DataField] public TimeSpan RequestLifetime { get; private set; } = TimeSpan.FromSeconds(20);
    [DataField] public TimeSpan CircuitOpenDuration { get; private set; } = TimeSpan.FromMinutes(2);
    [DataField] public TimeSpan SuggestionLifetime { get; private set; } = TimeSpan.FromSeconds(20);
    [DataField] public TimeSpan StrategicRequestCooldown { get; private set; } = TimeSpan.FromMinutes(2);
    [DataField] public TimeSpan IncidentEscalationCooldown { get; private set; } = TimeSpan.FromSeconds(30);
}

[Prototype("ncNeuralPersonaProfile")]
public sealed partial class NCNeuralPersonaProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string StableKey { get; private set; } = string.Empty;
    [DataField(required: true)] public string RoleKey { get; private set; } = string.Empty;
    [DataField(required: true)] public string OrganizationKey { get; private set; } = string.Empty;
    [DataField] public string Biography { get; private set; } = string.Empty;
    [DataField] public string[] PrincipleKeys { get; private set; } = [];
    [DataField] public string[] HabitKeys { get; private set; } = [];
}

[Prototype("ncNeuralAutonomyPolicy")]
public sealed partial class NCNeuralAutonomyPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string StableKey { get; private set; } = string.Empty;
    [DataField(required: true)] public NCNeuralRequestKind[] AllowedKinds { get; private set; } = [];
    [DataField] public int MinimumIntentConfidence { get; private set; } = 500;
    [DataField] public int MaximumIntentPriority { get; private set; } = 750;
    [DataField] public bool TargetedIntentsAllowed { get; private set; } = true;
}

[Prototype("ncNeuralPromptProfile")]
public sealed partial class NCNeuralPromptProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string StableKey { get; private set; } = default!;
    [DataField] public int Version { get; private set; } = 1;
    [DataField] public string Model { get; private set; } = "gpt-5.6-luna";
    [DataField] public IReadOnlyList<NCNeuralRequestKind> AllowedKinds { get; private set; } = [];
}
