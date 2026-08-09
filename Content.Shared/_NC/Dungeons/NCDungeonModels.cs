// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using Content.Shared._NC.City.ZoneTemplates;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Identity;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Dungeons;

[Serializable, NetSerializable]
public readonly record struct NCDungeonRunId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public static NCDungeonRunId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public enum NCDungeonRunState : byte
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

public enum NCDungeonRunResult : byte
{
    None,
    Success,
    Failure,
    Aborted,
}

public enum NCDungeonExtractionPolicy : byte
{
    DeleteOnCleanup,
    CanBeExtracted,
    ObjectiveOnly,
    PersistentWorldItem,
    NeverExtract,
}

public readonly record struct NCDungeonRunStatus(
    NCDungeonRunId RunId,
    NCDungeonSlotId SlotId,
    string TemplateId,
    NCZoneDeploymentId DeploymentId,
    NCDungeonRunState State,
    IReadOnlySet<NCCharacterId> Participants,
    int Seed,
    DateTime StartedAt,
    DateTime? CompletedAt,
    NCDungeonRunResult Result,
    string? Error);

public readonly record struct NCDungeonStartResult(bool Success, NCDungeonRunId RunId, string Error);
