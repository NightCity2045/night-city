// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Cognition;

/// <summary>One server-authored physical procedure that GPT may request by capability ID.</summary>
[Prototype("ncCognitionCapability")]
public sealed partial class NCCognitionCapabilityPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public NCCognitionDomain Domain { get; private set; }
    [DataField(required: true)] public NCCognitionTaskKind TaskKind { get; private set; }
    [DataField] public NCCognitionProcedureStep[] ProcedureSteps { get; private set; } = [];
    [DataField] public NCCognitionStrategicOption[] StrategicOptions { get; private set; } = [];
}

[DataDefinition]
public sealed partial class NCCognitionProcedureStep
{
    [DataField(required: true)] public string Id { get; private set; } = string.Empty;
    [DataField(required: true)] public string Action { get; private set; } = string.Empty;
    [DataField] public string[] DependsOn { get; private set; } = [];
    [DataField] public bool Optional { get; private set; }
}

/// <summary>One bounded alternative procedure supplied to the GPT command executor.</summary>
[DataDefinition]
public sealed partial class NCCognitionStrategicOption
{
    [DataField(required: true)] public string Id { get; private set; } = string.Empty;
    [DataField(required: true)] public float MinimumOutcome { get; private set; }
    [DataField(required: true)] public float MaximumOutcome { get; private set; }
    [DataField(required: true)] public NCCognitionProcedureStep[] ProcedureSteps { get; private set; } = [];
}

[Prototype("ncCognitionCapabilityProfile")]
public sealed partial class NCCognitionCapabilityProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField(required: true)] public ProtoId<NCCognitionCapabilityPrototype>[] Capabilities { get; private set; } = [];
}
