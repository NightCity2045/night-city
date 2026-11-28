// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Bank;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Dispatch;

/// <summary>
/// A city emergency service residents call from their PDA: NCPD or Trauma Team. Members of
/// <see cref="Department"/> receive its incidents in their dispatcher program and on the city map.
/// </summary>
[Prototype("ncEmergencyService")]
public sealed partial class NCEmergencyServicePrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name;

    /// <summary>Job department whose members answer the calls.</summary>
    [DataField(required: true)]
    public ProtoId<DepartmentPrototype> Department;

    /// <summary>Bank account paid calls and subscriptions are paid into.</summary>
    [DataField]
    public ProtoId<NCBankDepartmentPrototype>? Account;

    /// <summary>Price of one call for a caller without a subscription. Zero means calls are free.</summary>
    [DataField]
    public long CallPrice;

    /// <summary>Arrival target of a paid one-off call; late arrival refunds the call.</summary>
    [DataField]
    public TimeSpan? CallResponseTime;

    /// <summary>Time between two calls of one caller to this service.</summary>
    [DataField]
    public TimeSpan CallCooldown = TimeSpan.FromMinutes(1);

    /// <summary>Whether calls from districts without police coverage are marked as outside the patrol area.</summary>
    [DataField]
    public bool UsesCoverage;
}

/// <summary>
/// A Trauma Team subscription tier. The price is charged when the subscription starts and on every payday from
/// the subscriber's account; a crew arriving later than <see cref="ResponseTime"/> refunds one payment.
/// </summary>
[Prototype("ncTraumaPlan")]
public sealed partial class NCTraumaPlanPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name;

    [DataField(required: true)]
    public LocId Description;

    [DataField(required: true)]
    public long Price;

    [DataField(required: true)]
    public TimeSpan ResponseTime;

    /// <summary>Higher ranks are listed first in the dispatcher program.</summary>
    [DataField]
    public int Rank;
}
