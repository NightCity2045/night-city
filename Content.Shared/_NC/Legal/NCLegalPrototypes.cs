// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Shared._NC.Organizations;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Legal;

[Prototype("ncLicense")]
public sealed partial class NCLicensePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name { get; private set; }

    [DataField(required: true)]
    public LocId Description { get; private set; }

    [DataField]
    public ProtoId<NCOrganizationPrototype>? IssuingOrganization { get; private set; }

    /// <summary>
    /// Null means that the license does not expire automatically.
    /// </summary>
    [DataField]
    public int? ValidityDays { get; private set; }
}

[Prototype("ncDocument")]
public sealed partial class NCDocumentPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name { get; private set; }

    [DataField(required: true)]
    public LocId Description { get; private set; }

    [DataField]
    public ProtoId<NCOrganizationPrototype>? IssuingOrganization { get; private set; }

    [DataField]
    public int? ValidityDays { get; private set; }

    /// <summary>
    /// Documents with this flag are created by the identity flow rather than an RP issuer.
    /// </summary>
    [DataField]
    public bool IdentityDocument { get; private set; }
}
