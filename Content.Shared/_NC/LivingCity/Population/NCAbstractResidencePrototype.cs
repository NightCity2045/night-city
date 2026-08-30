// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.LivingCity.Actors;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Population;

/// <summary>
/// Data-driven kind of abstract housing. Weight is used only for deterministic initial assignment; an existing
/// resident keeps the persisted selection when prototypes are rebalanced.
/// </summary>
[Prototype("ncAbstractResidenceProfile")]
public sealed partial class NCAbstractResidenceProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public NCAbstractResidenceKind Kind { get; private set; }

    [DataField]
    public NCAbstractResidenceQuality Quality { get; private set; }

    [DataField]
    public int WeeklyCost { get; private set; }

    [DataField]
    public int SelectionWeight { get; private set; } = 1;

    [DataField]
    public List<NCZoneContextSlot> AllowedHomeContexts { get; private set; } =
        new()
        {
            NCZoneContextSlot.District,
            NCZoneContextSlot.Sector,
            NCZoneContextSlot.Street,
            NCZoneContextSlot.Building,
            NCZoneContextSlot.Apartment,
        };
}
