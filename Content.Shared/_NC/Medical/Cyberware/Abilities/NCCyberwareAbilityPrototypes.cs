// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Humanity;
using Content.Shared.Tools;
using Robust.Shared.Prototypes;
using Robust.Shared.Audio;

namespace Content.Shared._NC.Medical.Cyberware.Abilities;

public enum NCCyberwareAbilityKind : byte { Tool, Blade, Biomonitor, MotorBurst, Stabilization }

[Prototype("ncCyberwareAbility")]
public sealed partial class NCCyberwareAbilityPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public NCCyberwareAbilityKind Kind { get; private set; }
    [DataField(required: true)] public EntProtoId Action { get; private set; }
    [DataField] public EntProtoId? IntegralItem { get; private set; }
    [DataField] public float StartupEnergy { get; private set; }
    [DataField] public float DurationSeconds { get; private set; } = 6f;
    [DataField] public float CooldownSeconds { get; private set; } = 30f;
    [DataField] public float MotorBonus { get; private set; } = 0.25f;
    [DataField] public float ManualDurationFactor { get; private set; } = 0.8f;
    [DataField] public float MovingPenaltyReduction { get; private set; } = 0.25f;
    [DataField] public ProtoId<NCHumanityStimulusPrototype>? ActiveStress { get; private set; }
    [DataField] public SoundSpecifier? ActivationSound { get; private set; }
    [DataField] public SoundSpecifier? RetractionSound { get; private set; }
}

[Prototype("ncCyberwareAbilityPolicy")]
public sealed partial class NCCyberwareAbilityPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public float MaximumMotorBonus { get; private set; } = 0.25f;
    [DataField] public float MinimumManualDurationFactor { get; private set; } = 0.8f;
    [DataField] public float MaximumPenaltyReduction { get; private set; } = 0.25f;
    [DataField] public float MaximumWalkSpeed { get; private set; } = 4.5f;
    [DataField] public float MaximumSprintSpeed { get; private set; } = 6f;
    [DataField] public List<ProtoId<ToolQualityPrototype>> ManualMotorQualities { get; private set; } = [];
}
