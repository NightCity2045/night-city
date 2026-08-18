// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.City.Zones.Serialization;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Housing.Prototypes;

/// <summary>Bounded runtime scheduling limits for canonical Housing work.</summary>
[Prototype("ncPropertyRuntimeConfiguration")]
public sealed partial class NCPropertyRuntimeConfigurationPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField(required: true)]
    public int MaximumRentOperationsPerTick { get; private set; } = 4;

    [DataField(required: true)]
    public int MaximumConcurrentRentOperations { get; private set; } = 4;
}

/// <summary>
/// Static rental configuration for one apartment zone. Mutable tenancy remains in the database.
/// </summary>
[Prototype("ncPropertyListing")]
public sealed partial class NCPropertyListingPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true, customTypeSerializer: typeof(NCApartmentIdSerializer))]
    public NCApartmentId ApartmentId { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCPropertyAccessPolicyPrototype> AccessPolicy { get; private set; }

    [DataField]
    public bool StartingHousingEligible { get; private set; }

    [DataField]
    public List<NCPropertyInteriorOffer> Interiors { get; private set; } = [];
}

/// <summary>
/// One selectable apartment design and the recurring rent policy charged for it.
/// </summary>
[DataDefinition]
public sealed partial class NCPropertyInteriorOffer
{
    [DataField(required: true)]
    public ProtoId<NCZoneTemplatePrototype> Template { get; private set; }

    [DataField(required: true)]
    public ProtoId<NCRentPolicyPrototype> RentPolicy { get; private set; }
}

/// <summary>
/// Data-driven recurring rent and grace-period rules.
/// </summary>
[Prototype("ncRentPolicy")]
public sealed partial class NCRentPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public int RentAmount { get; private set; }

    [DataField(required: true)]
    public TimeSpan BillingInterval { get; private set; }

    [DataField(required: true)]
    public TimeSpan GracePeriod { get; private set; }

}

/// <summary>
/// Key-card issuance limits and default permissions for a property.
/// </summary>
[Prototype("ncPropertyAccessPolicy")]
public sealed partial class NCPropertyAccessPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public NCPropertyPermission TenantPermissions { get; private set; }

    [DataField(required: true)]
    public int MaximumActiveCards { get; private set; }

    [DataField(required: true)]
    public EntProtoId KeyCardPrototype { get; private set; }
}
