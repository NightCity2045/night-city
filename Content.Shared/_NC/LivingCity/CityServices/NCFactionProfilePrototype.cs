// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Serialization;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.CityServices;

public enum NCFactionArchetype : byte
{
    StreetGang,
    CrimeSyndicate,
    Community,
    Nomad,
    Political,
    Security,
    Scavenger,
    Cult,
}

/// <summary>
/// Authored faction identity and initial strategic state. Runtime ownership remains in NCFactionRegistrySystem.
/// </summary>
[Prototype("ncFactionProfile")]
public sealed partial class NCFactionProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public string StableKey { get; private set; } = default!;

    [DataField(required: true)]
    public string DisplayName { get; private set; } = default!;

    [DataField(required: true)]
    public NCFactionArchetype Archetype { get; private set; }

    [DataField(required: true)]
    public List<string> IdeologyKeys { get; private set; } = [];

    [DataField]
    public long StartingFunds { get; private set; }

    [DataField]
    public int AvailableManpower { get; private set; }

    [DataField]
    public int TaxBasisPoints { get; private set; }

    [DataField]
    public int ProtectionBasisPoints { get; private set; }

    [DataField]
    public int PatrolPriority { get; private set; }

    [DataField]
    public int SupplyPriority { get; private set; }

    [DataField]
    public bool EnforcesLaw { get; private set; }

    [DataField]
    public bool UsesCovertOperations { get; private set; }

    [DataField]
    public List<NCFactionTerritoryProfile> Territories { get; private set; } = [];

    [DataField]
    public List<NCFactionRelationProfile> Relations { get; private set; } = [];
}

[DataDefinition]
public sealed partial class NCFactionTerritoryProfile
{
    [DataField(required: true, customTypeSerializer: typeof(NCZoneIdSerializer))]
    public NCZoneId Zone { get; private set; }

    [DataField(required: true)]
    public int Influence { get; private set; }

    [DataField]
    public bool ClaimsControl { get; private set; }
}

[DataDefinition]
public sealed partial class NCFactionRelationProfile
{
    [DataField(required: true)]
    public ProtoId<NCFactionProfilePrototype> Faction { get; private set; }

    [DataField(required: true)]
    public NCFactionRelationStatus Status { get; private set; }

    [DataField]
    public int Trust { get; private set; }

    [DataField]
    public int Hostility { get; private set; }
}
