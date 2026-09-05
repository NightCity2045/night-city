// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Neural;
using Content.Shared.Humanoid.Prototypes;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Actors;

/// <summary>
/// Data-driven inputs for one-click creation of an ordinary persistent resident.
/// </summary>
[Prototype("ncRandomResidentGenerationProfile")]
public sealed partial class NCRandomResidentGenerationProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public ProtoId<NCCityActorArchetypePrototype> Archetype { get; private set; }

    [DataField(required: true)]
    public ProtoId<SpeciesPrototype> Species { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCNeuralPersonaProfilePrototype>[] Personas { get; private set; } = [];

    [DataField(required: true)] public string[] BackgroundKeys { get; private set; } = [];
    [DataField(required: true)] public string[] PressureKeys { get; private set; } = [];
    [DataField(required: true)] public string[] FearKeys { get; private set; } = [];
    [DataField(required: true)] public string[] GoalKeys { get; private set; } = [];
    [DataField(required: true)] public string[] SocialHabitKeys { get; private set; } = [];
    [DataField(required: true)] public string[] SpeechQuirkKeys { get; private set; } = [];
}
