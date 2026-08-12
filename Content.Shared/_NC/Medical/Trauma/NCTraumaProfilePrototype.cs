// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Trauma;

/// <summary>
/// Identifies the data-driven mechanism that converts one resolved impact into local wounds.
/// Numerical conversion remains in wound and physiology profiles so this contract carries no hidden balance.
/// </summary>
[Prototype("ncTraumaProfile")]
public sealed partial class NCTraumaProfilePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public NCTraumaMechanism Mechanism { get; private set; }
}

public enum NCTraumaMechanism : byte
{
    Blunt,
    Slash,
    Pierce,
    Projectile,
    Heat,
    Cold,
    Electrical,
    Explosive,
    Pressure,
    Chemical,
}
