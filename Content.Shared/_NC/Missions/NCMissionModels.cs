// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Identity;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Missions;

[Serializable, NetSerializable]
public readonly record struct NCMissionRunId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public static NCMissionRunId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public enum NCMissionRunState : byte
{
    Requested,
    Preparing,
    Ready,
    Active,
    Completed,
    Failed,
    Aborted,
    Closing,
    Cleaning,
    Closed,
}

[Serializable, NetSerializable]
public enum NCMissionRunResult : byte
{
    None,
    Success,
    Failure,
    Aborted,
}

[Serializable, NetSerializable]
public enum NCMissionExtractionPolicy : byte
{
    DeleteOnCleanup,
    CanBeExtracted,
    ObjectiveOnly,
    PersistentWorldItem,
    NeverExtract,
}

public readonly record struct NCMissionRunStatus(
    NCMissionRunId RunId,
    NCMissionSlotId SlotId,
    string TemplateId,
    NCZoneDeploymentId DeploymentId,
    NCMissionRunState State,
    IReadOnlySet<NCCharacterId> Participants,
    int Seed,
    DateTime StartedAt,
    DateTime ExpiresAt,
    DateTime WarningAt,
    DateTime? CompletedAt,
    NCMissionRunResult Result,
    string? Error);

public readonly record struct NCMissionStartResult(bool Success, NCMissionRunId RunId, string Error);
