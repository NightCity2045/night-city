// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.City.ZoneTemplates;

/// <summary>
/// Stable identity of one idempotent deployment attempt.
/// </summary>
[Serializable, NetSerializable]
public readonly record struct NCZoneDeploymentId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public override string ToString() => Value.ToString("D");
    public static NCZoneDeploymentId New() => new(Guid.NewGuid());
}

public enum NCZoneDeploymentPurpose : byte
{
    ApartmentInterior,
    DungeonRun,
    Administrative,
}

public enum NCZoneDeploymentState : byte
{
    Pending,
    Validating,
    LockingZone,
    CapturingBaseline,
    Clearing,
    ApplyingTiles,
    SpawningStaticEntities,
    ResolvingVerticalLinks,
    SpawningDynamicContent,
    Initializing,
    Active,
    ClosingEntrances,
    Evacuating,
    ExtractingLoot,
    Cleaning,
    RestoringBaseline,
    Completed,
    Failed,
    RollingBack,
    FailedClean,
}
