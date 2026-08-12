// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.ActionResolution;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Emergency;

/// <summary>
/// Server-owned emergency support state for one authoritative NC medical body. Timed interventions are
/// advanced only by the central physiology scheduler; they never run their own world scan or Update loop.
/// </summary>
[RegisterComponent]
public sealed partial class NCEmergencyCareStateComponent : Component
{
    [DataField]
    public ProtoId<NCEmergencyCareProfilePrototype> Profile;

    [DataField]
    public TimeSpan CprRemaining;

    [DataField]
    public TimeSpan OxygenRemaining;

    [DataField]
    public TimeSpan VentilationRemaining;

    /// <summary>
    /// Non-oxygen-carrying circulating volume supplied by crystalloid. Blood volume remains authoritative
    /// on NCCirculationComponent and is never synthesized from this value.
    /// </summary>
    [DataField]
    public float CrystalloidVolume;

    /// <summary>
    /// Bounded oldest-first action IDs prevent network retries or restored actions from applying twice.
    /// </summary>
    [DataField]
    public List<NCActionResolutionId> ProcessedActions = [];

    [DataField]
    public List<NCEmergencyCareAuditRecord> AuditHistory = [];
}

/// <summary>
/// Data-only capability and charge state for a physical emergency-care item or device.
/// </summary>
[RegisterComponent]
public sealed partial class NCEmergencyCareItemComponent : Component
{
    [DataField(required: true)]
    public ProtoId<NCEmergencyCareCapabilityPrototype> Capability;

    // Devices backed by vanilla power (for example defibrillators) intentionally do not use
    // a second NC charge counter. Consumable emergency items still configure this field in YAML.
    [DataField]
    public int ChargesRemaining;
}
