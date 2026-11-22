// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Chemistry.Reagent;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Pharmacology.Lab;

/// <summary>
/// A kind of raw material. Every compound is made from one main feedstock and, with a poorer yield, from a few
/// others, so what a laboratory can make depends on what it can get.
/// </summary>
[Prototype("ncPharmaFeedstock")]
public sealed partial class NCPharmaFeedstockPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name { get; private set; }

    [DataField(required: true)]
    public LocId Description { get; private set; }

    /// <summary>
    /// The reagent a brew of this feedstock turns into. The extractor reads it out of a beaker, so a player
    /// who brews raw material makes exactly this kind.
    /// </summary>
    [DataField(required: true)]
    public ProtoId<ReagentPrototype> Broth { get; private set; }

    /// <summary>
    /// The reagent a source canister of this feedstock holds. What the extractor makes out of a broth is this
    /// reagent, and the synthesizer and the reactor spend it.
    /// </summary>
    [DataField(required: true)]
    public ProtoId<ReagentPrototype> Source { get; private set; }

    /// <summary>The reagent that is brewed into the broth. The console shows how to make it.</summary>
    [DataField(required: true)]
    public ProtoId<ReagentPrototype> Substrate { get; private set; }
}

/// <summary>One feedstock a formula can be made from and what share of the doses it gives.</summary>
[Serializable, NetSerializable]
public readonly record struct NCPharmaFeedstockOption(string Id, float Yield);
