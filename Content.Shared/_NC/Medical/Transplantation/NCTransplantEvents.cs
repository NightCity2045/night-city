// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Medical.Transplantation;

/// <summary>
/// Raised on the recipient body after a detached live subtree is physically attached.
/// </summary>
[ByRefEvent]
public readonly record struct NCOrganAttachedEvent(EntityUid OrganRoot);

[ByRefEvent]
public readonly record struct NCTransplantRejectionChangedEvent(
    EntityUid OrganRoot,
    NCTransplantRejectionStage PreviousStage,
    NCTransplantRejectionStage CurrentStage,
    float Burden);
