// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.Employment;

/// <summary>
/// Operational limits shared by NPC employment bootstrap. Job identity and salary remain authoritative on the
/// existing player-facing JobPrototype.
/// </summary>
[Prototype("ncCityWorkforcePolicy")]
public sealed partial class NCCityWorkforcePolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public TimeSpan PayrollInterval { get; private set; }

    [DataField]
    public int MaximumEmployees { get; private set; } = 20_000;

    /// <summary>Bounded keyset page used to rebuild the in-memory workforce index after a server restart.</summary>
    [DataField]
    public int RecoveryPageSize { get; private set; } = 100;

    /// <summary>Safety ceiling for historical employment rows inspected during one recovery pass.</summary>
    [DataField]
    public int MaximumRecoveryRecords { get; private set; } = 100_000;
}
