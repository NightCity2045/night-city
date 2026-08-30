// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.LivingCity.CityServices;

[Prototype("ncDistrictSafetyPolicy")]
public sealed partial class NCDistrictSafetyPolicyPrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = default!;
    [DataField] public int SeverityThreatPermille { get; private set; } = 1000;
    [DataField] public int IncidentThreat { get; private set; } = 25;
    [DataField] public int EnforcedLawPressure { get; private set; } = 400;
    [DataField] public int PatrolPressurePermille { get; private set; } = 1000;
    [DataField] public int PoliceDispatchPressure { get; private set; } = 50;
    [DataField] public int MaximumProjectionValue { get; private set; } = 10_000;
}
