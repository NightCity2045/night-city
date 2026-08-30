// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Economy;

[Serializable, NetSerializable]
public readonly record struct NCCommodityBatchId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public static NCCommodityBatchId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public readonly record struct NCStockNodeId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public static NCStockNodeId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public readonly record struct NCEconomicReservationId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public static NCEconomicReservationId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public readonly record struct NCEconomicQuoteId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public static NCEconomicQuoteId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public readonly record struct NCEconomicTransferId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public static NCEconomicTransferId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

[Serializable, NetSerializable]
public readonly record struct NCEconomicFulfillmentId(Guid Value)
{
    public bool IsValid => Value != Guid.Empty;
    public static NCEconomicFulfillmentId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}
