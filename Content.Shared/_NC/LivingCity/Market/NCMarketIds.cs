// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Market;

[Serializable, NetSerializable]
public readonly record struct NCMarketListingId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public static NCMarketListingId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public readonly record struct NCMarketRequestId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public static NCMarketRequestId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public readonly record struct NCProductionJobId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public static NCProductionJobId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public readonly record struct NCShipmentId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public static NCShipmentId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public readonly record struct NCDisputeId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public static NCDisputeId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}
