// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Diagnostics.Detail;

/// <summary>
/// Server-observed disclosure, range and bounded-record policy for one examination method or device.
/// </summary>
[Prototype("ncDiagnosticCapability")]
public sealed partial class NCDiagnosticCapabilityPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public NCDiagnosticTier Tier { get; private set; }

    [DataField(required: true)]
    public NCDiagnosticDisclosure Disclosures { get; private set; }

    [DataField]
    public bool UnassistedDefault { get; private set; }

    [DataField]
    public bool SelfOnly { get; private set; }

    [DataField]
    public bool RequiresHeld { get; private set; }

    [DataField]
    public bool RequiresPower { get; private set; }

    [DataField]
    public float Range { get; private set; } = 1.5f;

    [DataField]
    public int MaxRegionRecords { get; private set; } = 12;

    [DataField]
    public int MaxWoundRecords { get; private set; }

    [DataField]
    public int MaxOrganRecords { get; private set; }

    [DataField]
    public int MaxCyberwareRecords { get; private set; }

    [DataField]
    public int MaxDrugRecords { get; private set; }

    [DataField]
    public float BloodVolumeUnitsQuantum { get; private set; } = 60f;

    [DataField]
    public float RatioQuantum { get; private set; } = 0.1f;

    [DataField]
    public float BleedRateQuantum { get; private set; } = 0.25f;

    [DataField]
    public float DrugConcentrationQuantum { get; private set; } = 0.1f;
}

/// <summary>
/// Physical diagnostic capability attached to an actual tool, scanner or console instance.
/// </summary>
[RegisterComponent]
public sealed partial class NCDiagnosticCapabilityComponent : Component
{
    [DataField(required: true)]
    public ProtoId<NCDiagnosticCapabilityPrototype> Capability;

    [DataField]
    public bool Operational = true;
}
