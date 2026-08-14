// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Surgery;

/// <summary>
/// Sanitized results for direct physical surgery interactions.
/// </summary>
[Serializable, NetSerializable]
public enum NCSurgeryInteractionNotice : byte
{
    None,
    NoPatient,
    OutOfRange,
    FacilityUnavailable,
    SelectTargetZone,
    NoCompatibleProcedure,
    SelectionRejected,
    SelectionBound,
    GraftBound,
    SelectProcedure,
    HoldRequiredTool,
    ToolUnsuitable,
    StepStarted,
    StepCancelled,
    StepUnavailable,
    ProcedureCompleted,
}
