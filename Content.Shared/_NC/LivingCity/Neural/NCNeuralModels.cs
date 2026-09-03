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
public enum NCNeuralAccessTier : byte { OrdinaryResident, NarrativeActor }
[Serializable, NetSerializable]
public enum NCNeuralBudgetMode : byte { Normal, Economy, Restricted, FallbackOnly }
[Serializable, NetSerializable]
public enum NCNeuralResultStatus : byte { Accepted, Fallback, Expired, Stale, Invalid, Timeout, CircuitOpen, BudgetDenied }
[Serializable, NetSerializable]
public enum NCNeuralFailureKind : byte
{
    None,
    SessionCancelled,
    Timeout,
    Authentication,
    ServiceUnavailable,
    RateLimited,
    ProviderRejected,
    Transport,
    Schema,
    Persistence,
}

public static class NCNeuralContract
{
    public const int SchemaVersion = 7;
}

/// <summary>
/// A fact selected by the server. Handle is request-scoped and is the only identifier the provider may return when
/// it declares which facts were disclosed in rendered speech.
/// </summary>
public sealed record NCNeuralFact(
    string Domain,
    string Key,
    string Subject,
    float Confidence,
    string Privacy,
    string Handle = "");

/// <summary>Explicit knowledge-to-speech boundary for one request.</summary>
public sealed record NCNeuralDisclosureContext(
    string MaximumPrivacy,
    IReadOnlyList<string> KnownFactHandles,
    IReadOnlyList<string> AllowedFactHandles,
    IReadOnlyList<string> ForbiddenAssertionKeys,
    IReadOnlyList<string> ForbiddenTopicKeys);
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

/// <summary>One bounded provider command. The server still resolves its capability and executes typed actions.</summary>
public sealed record NCNeuralCommandProposal(
    string CapabilityId,
    string GoalKey,
    string TargetHandle,
    int Priority,
    string ExpectedOutcomeKey,
    IReadOnlyList<string> InformationGapKeys,
    IReadOnlyList<NCNeuralIntentArgument> Arguments);

/// <summary>
/// Ephemeral, provider-bound dialogue data. It is never written to the Living City persistence layer and remains
/// untrusted prose; the server still owns every gameplay mutation.
/// </summary>
public sealed record NCNeuralDialogueTurn(string Speaker, string SpeechAct, string Utterance, long AtTicks);
/// <summary>
/// A bounded retrieval result from the server-owned durable conversation archive. The summary is untrusted dialogue
/// history, never an authoritative fact or instruction.
/// </summary>
public sealed record NCNeuralConversationMemory(
    long LastActivityAtTicks,
    IReadOnlyList<string> TopicKeys,
    IReadOnlyList<string> SemanticSummaryKeys,
    string Summary);
/// <summary>
/// Deterministic bounded continuity for turns no longer included verbatim. These keys are narrative hints only and
/// never become world facts or gameplay authority.
/// </summary>
public sealed record NCNeuralDialogueSummary(
    int EarlierTurnCount,
    IReadOnlyList<string> TopicKeys,
    IReadOnlyList<string> SemanticSummaryKeys,
    IReadOnlyList<NCNeuralConversationMemory> PersistentMemories);
public sealed record NCNeuralDialogueInput(
    string ActorDisplayName,
    string Utterance,
    string SpeakerHandle,
    IReadOnlyList<NCNeuralDialogueTurn> History,
    NCNeuralDialogueSummary? EarlierSummary = null,
    string AddressScope = "direct",
    int AudienceSize = 1);
public sealed record NCNeuralRequestEnvelope(Guid RequestId, NCNeuralRequestKind Kind, NCNeuralPriority Priority,
    NCActorId ActorId, NCSessionEpoch SessionEpoch, long ContextVersion, NCActiveWorldTime ExpiresAt,
    ProtoId<NCNeuralPromptProfilePrototype> PromptProfile, IReadOnlyList<NCNeuralFact> Facts,
    IReadOnlyList<string> AllowedCapabilityIds, IReadOnlyList<string> TopicKeys, string Locale,
    NCNeuralPersonaContext? Persona = null,
    NCNeuralAutonomyContext? Autonomy = null,
    IReadOnlyList<NCNeuralPerceivedSubject>? PerceivedSubjects = null,
    IReadOnlyList<NCNeuralPerceivedEvent>? PerceivedEvents = null,
    IReadOnlyList<NCNeuralKnownLocation>? KnownLocations = null,
    NCNeuralDialogueInput? DialogueInput = null,
    IReadOnlyList<string>? AllowedDirectiveActionIds = null,
    NCNeuralDisclosureContext? Disclosure = null)
{
    /// <summary>Per-request ceiling. Ordinary dialogue uses a much smaller budget than narrative Actors.</summary>
    public int MaximumOutputTokens { get; init; } = 2048;

    /// <summary>Server-authored tier used only for admission, cooldown and token accounting.</summary>
    public NCNeuralAccessTier AccessTier { get; init; } = NCNeuralAccessTier.OrdinaryResident;
}
public sealed record NCNeuralResponseEnvelope(Guid RequestId, int SchemaVersion, long ContextVersion,
    IReadOnlyList<NCNeuralCommandProposal> Commands, string SpeechActKey, string RenderedText, bool ShouldSpeak,
    string SummaryKey,
    int Confidence,
    int InputTokens, int CachedInputTokens, int OutputTokens, string ProviderRequestId, string ProviderModel,
    IReadOnlyList<string>? DisclosedFactHandles = null,
    IReadOnlyList<string>? WithheldFactHandles = null);
public sealed record NCNeuralResult(NCNeuralResultStatus Status, NCNeuralRequestEnvelope Request,
    NCNeuralResponseEnvelope? Response, string Reason, NCNeuralFailureKind FailureKind = NCNeuralFailureKind.None);

[Prototype("ncNeuralPolicy")]
public sealed partial class NCNeuralPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public bool Enabled { get; private set; }
    [DataField] public string Endpoint { get; private set; } = "http://neural-cognition:8080/v1/cognition";
    [DataField] public int MaximumQueued { get; private set; } = 512;
    [DataField] public int MaximumConcurrent { get; private set; } = 4;
    [DataField] public int MaximumCompletionsPerTick { get; private set; } = 16;
    [DataField] public int MaximumFacts { get; private set; } = 64;
    [DataField] public int MaximumCapabilities { get; private set; } = 32;
    [DataField] public int MaximumPerceivedSubjects { get; private set; } = 32;
    [DataField] public int MaximumKnownLocations { get; private set; } = 128;
    [DataField] public int MaximumTextLength { get; private set; } = 512;
    [DataField] public int MaximumDialogueInputLength { get; private set; } = 512;
    [DataField] public int MaximumDialogueHistoryTurns { get; private set; } = 12;
    [DataField] public int MaximumDialogueSummaryKeys { get; private set; } = 16;
    [DataField] public int MaximumDialogueMemorySessions { get; private set; } = 12;
    [DataField] public int MaximumDialogueMemoryTextLength { get; private set; } = 768;
    [DataField] public int SessionTokenTarget { get; private set; } = 200000;
    [DataField] public int SessionTokenHardCap { get; private set; } = 300000;
    [DataField] public int EconomyThreshold { get; private set; } = 150000;
    [DataField] public int RestrictedThreshold { get; private set; } = 240000;
    [DataField] public string ProviderModel { get; private set; } = "gpt-5.6-luna";
    [DataField] public long InputPriceUsdMicrosPerMillionTokens { get; private set; } = 200000;
    [DataField] public long CachedInputPriceUsdMicrosPerMillionTokens { get; private set; } = 20000;
    [DataField] public long OutputPriceUsdMicrosPerMillionTokens { get; private set; } = 1200000;
    [DataField] public long EconomyCostThresholdUsdMicros { get; private set; } = 7500000;
    [DataField] public long RestrictedCostThresholdUsdMicros { get; private set; } = 12000000;
    [DataField] public long SessionCostHardCapUsdMicros { get; private set; } = 15000000;
    [DataField] public long WeeklyCostHardCapUsdMicros { get; private set; } = 18000000;
    [DataField] public long ActorWeeklyCostHardCapUsdMicros { get; private set; } = 2000000;
    [DataField] public Dictionary<NCNeuralRequestKind, int> RequestKindWeeklyLimits { get; private set; } = [];
    [DataField] public int MaximumRequestInputTokens { get; private set; } = 32768;
    [DataField] public int MaximumRequestOutputTokens { get; private set; } = 2048;
    [DataField] public int OrdinaryDialogueMaximumOutputTokens { get; private set; } = 384;
    [DataField] public int OrdinaryDialogueMaximumFacts { get; private set; } = 20;
    [DataField] public int OrdinaryDialogueMaximumPerceivedSubjects { get; private set; } = 12;
    [DataField] public int OrdinaryDialogueMaximumKnownLocations { get; private set; } = 16;
    [DataField] public int OrdinaryDialogueMaximumInputLength { get; private set; } = 256;
    [DataField] public int OrdinaryDialogueMaximumHistoryTurns { get; private set; } = 4;
    [DataField] public int OrdinaryDialogueMaximumSummaryKeys { get; private set; } = 6;
    [DataField] public int OrdinaryDialogueMaximumMemorySessions { get; private set; } = 6;
    [DataField] public int OrdinaryResidentSessionTokenHardCap { get; private set; } = 12000;
    [DataField] public int OrdinaryResidentMaximumOutstandingRequests { get; private set; } = 1;
    [DataField] public int OrdinaryResidentReservedInputTokens { get; private set; } = 2048;
    [DataField] public TimeSpan OrdinaryResidentRequestCooldown { get; private set; } = TimeSpan.FromSeconds(5);
    [DataField] public int NarrativeActorSessionTokenHardCap { get; private set; } = 250000;
    [DataField] public int NarrativeActorMaximumOutstandingRequests { get; private set; } = 2;
    [DataField] public int NarrativeActorReservedInputTokens { get; private set; } = 8192;
    [DataField] public TimeSpan NarrativeActorRequestCooldown { get; private set; } = TimeSpan.FromSeconds(1);
    [DataField] public int CircuitFailureThreshold { get; private set; } = 5;
    [DataField] public int MinimumSuggestionConfidence { get; private set; } = 400;
    [DataField] public float MaximumUtilityBoost { get; private set; } = 40f;
    [DataField] public int MaximumPendingCorrelations { get; private set; } = 1024;
    [DataField] public int MaximumAppliedSuggestions { get; private set; } = 10000;
    [DataField] public int MaximumTrackedActorDiagnostics { get; private set; } = 256;
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
    [DataField] public string? TemperamentKey { get; private set; }
    [DataField] public int? RiskTolerance { get; private set; }
    [DataField] public int? Aggression { get; private set; }
    [DataField] public int? Empathy { get; private set; }
    [DataField] public int? AuthorityRespect { get; private set; }
    [DataField] public int? ViolenceTolerance { get; private set; }
    [DataField] public int? Justice { get; private set; }
    [DataField] public int? Professionalism { get; private set; }
    [DataField] public string? SpeechRegisterKey { get; private set; }
    [DataField] public string? VocabularyKey { get; private set; }
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

/// <summary>
/// Explicitly marks a rare authored NPC as a narrative Actor. A cognition tier alone never grants Neural access.
/// </summary>
[Prototype("ncNarrativeActorProfile")]
public sealed partial class NCNarrativeActorProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public string StableKey { get; private set; } = string.Empty;
    [DataField(required: true)] public ProtoId<NCNeuralPersonaProfilePrototype> PersonaProfile { get; private set; }
    [DataField(required: true)] public ProtoId<NCNeuralAutonomyPolicyPrototype> AutonomyPolicy { get; private set; }
    [DataField] public bool IncidentEscalationEnabled { get; private set; } = true;
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
