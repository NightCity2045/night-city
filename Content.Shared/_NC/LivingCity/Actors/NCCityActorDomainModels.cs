// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared._NC.LivingCity.Economy;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Actors;

[Serializable, NetSerializable]
public enum NCCityOverallCondition : byte
{
    Stable,
    Compensating,
    Critical,
    ClinicalArrest,
    Dead,
}

[Serializable, NetSerializable]
public enum NCCityBleedingState : byte
{
    None,
    Controlled,
    External,
    Internal,
    Combined,
    Arterial,
}

[Serializable, NetSerializable]
public enum NCCityMobilityState : byte
{
    Mobile,
    Impaired,
    Immobile,
}

[Serializable, NetSerializable]
public enum NCCityConsciousnessState : byte
{
    Alert,
    Dazed,
    Confused,
    Stuporous,
    Unconscious,
    Comatose,
    BrainDead,
}

[Flags, Serializable, NetSerializable]
public enum NCCityTreatmentState : ushort
{
    None = 0,
    Pressure = 1 << 0,
    Bandaged = 1 << 1,
    Hemostatic = 1 << 2,
    Clamped = 1 << 3,
    Tourniqueted = 1 << 4,
    Cauterized = 1 << 5,
    Debrided = 1 << 6,
    Drained = 1 << 7,
}

[Serializable, NetSerializable]
public enum NCCityCyberwareState : byte
{
    None,
    Operational,
    Degraded,
    Disabled,
}

[Flags, Serializable, NetSerializable]
public enum NCCityInventoryItemCategory : ushort
{
    None = 0,
    Unique = 1 << 0,
    Weapon = 1 << 1,
    Document = 1 << 2,
    Credential = 1 << 3,
    Commodity = 1 << 4,
}

/// <summary>
/// Strategic projection of the complete NC medical graph. Detailed treatment remains medical-domain data.
/// </summary>
public sealed record NCCityActorHealthSummary(
    NCCityOverallCondition OverallCondition,
    NCCityBleedingState Bleeding,
    NCCityMobilityState Mobility,
    NCCityConsciousnessState Consciousness,
    Guid[] CriticalWoundIds,
    NCCityTreatmentState ActiveTreatment,
    NCCityCyberwareState Cyberware,
    NCActiveWorldTime? RecoveryUntil);

/// <summary>
/// Actor health aggregate. The opaque payload is the canonical full medical snapshot used on materialization.
/// </summary>
public sealed record NCCityActorHealthSnapshot(
    NCActorId ActorId,
    NCCityActorHealthSummary Summary,
    ushort MedicalSnapshotVersion,
    string? MedicalStatePayload,
    NCActiveWorldTime CapturedAt,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCCityCommodityBundle(NCCommodityBatchId BatchId, string CommodityId, long Units);

/// <summary>
/// Stable economy-owned references held by an actor. Runtime EntityUid values never cross this boundary.
/// </summary>
public sealed record NCCityActorInventorySummary(
    NCItemId[] UniqueItems,
    NCItemId[] Weapons,
    NCItemId[] Documents,
    NCItemId[] Credentials,
    NCCityCommodityBundle[] Commodities,
    NCItemId[] InstalledCyberware,
    string EquippedLoadoutId);

public sealed record NCCityActorInventorySnapshot(
    NCActorId ActorId,
    NCCityActorInventorySummary Summary,
    long SourceAuthorityVersion,
    NCActiveWorldTime CapturedAt,
    int DataVersion,
    Guid LastAppliedEventId);
