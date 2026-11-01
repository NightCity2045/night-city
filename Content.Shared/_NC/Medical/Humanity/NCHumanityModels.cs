// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Humanity;

[Serializable, NetSerializable]
public enum NCCyberCrisisStage : byte { Stable, Warning, Crisis, Acute }

/// <summary>Character state carried by the one living brain, never by a replaceable frame or implant.</summary>
[RegisterComponent]
public sealed partial class NCHumanityComponent : Component
{
    [DataField] public NCHumanityStateSnapshot State = new();
    [ViewVariables] public float Current => State.Current;
    [ViewVariables] public float Burden => State.Burden;
    [ViewVariables] public float Stress => State.Stress;
    [ViewVariables] public NCCyberCrisisStage Stage => State.Stage;
    [ViewVariables] public bool Restoring;
}

[DataDefinition]
public sealed partial class NCHumanityStateSnapshot
{
    [DataField(required: true)] public float Current = NCHumanityMath.Base;
    [DataField(required: true)] public float Burden;
    [DataField(required: true)] public float Stress;
    [DataField(required: true)] public NCCyberCrisisStage Stage;
    [DataField(required: true)] public float StabilizingSeconds;
    [DataField(required: true)] public Dictionary<string, float> Adaptation = [];
    [DataField(required: true)] public List<NCHumanityImpulseMemory> RecentImpulses = [];
}

[DataDefinition]
public sealed partial class NCHumanityImpulseMemory
{
    [DataField(required: true)] public Guid Source;
    [DataField(required: true)] public ProtoId<NCHumanityStimulusPrototype> Profile;
    [DataField(required: true)] public float RemainingSeconds;
}

public readonly record struct NCHumanityPackageDose(float Burden, float AcuteCost);

[Serializable, NetSerializable]
public readonly record struct NCHumanityStatus(float Current, float Maximum, float Burden, float Stress,
    NCCyberCrisisStage Stage, float EnhancementEfficiency);

/// <summary>Runs after the scheduler has completed pharmacology, circulation and physiology for this interval.</summary>
[ByRefEvent]
public readonly record struct NCMedicalIntervalCompletedEvent(TimeSpan Elapsed);
