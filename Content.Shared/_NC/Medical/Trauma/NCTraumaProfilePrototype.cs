// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;
using Content.Shared._NC.Medical.Wounds;

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

    /// <summary>
    /// Optional wound behavior override for special physical payloads such as expanding ammunition.
    /// The mechanism remains useful for generic maps while all balance stays in the wound prototype.
    /// </summary>
    [DataField]
    public ProtoId<NCWoundPrototype>? WoundPrototype { get; private set; }
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
