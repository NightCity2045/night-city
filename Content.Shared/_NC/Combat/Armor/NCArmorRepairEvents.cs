// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.DoAfter;
using Content.Shared.FixedPoint;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Combat.Armor;

/// <summary>
/// Directed completion event for one server-authoritative armor field repair.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class NCArmorRepairDoAfterEvent : SimpleDoAfterEvent;

public enum NCArmorRepairOutcome : byte
{
    Applied,
    InvalidTarget,
    Depleted,
    NoCompatibleSection,
    AtRepairLimit,
}

/// <summary>
/// Deterministic result exposed to tests and future workshop integrations.
/// </summary>
public readonly record struct NCArmorRepairResult(
    NCArmorRepairOutcome Outcome,
    ProtoId<NCArmorSectionPrototype>? Section,
    FixedPoint2 PreviousSp,
    FixedPoint2 CurrentSp,
    int ChargesConsumed);

/// <summary>
/// Raised after a repair charge has been consumed and section SP has changed.
/// </summary>
public readonly record struct NCArmorRepairedEvent(
    ProtoId<NCArmorSectionPrototype> Section,
    FixedPoint2 PreviousSp,
    FixedPoint2 CurrentSp);
