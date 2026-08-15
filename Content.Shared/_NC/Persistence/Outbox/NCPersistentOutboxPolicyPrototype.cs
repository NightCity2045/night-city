// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Shared._NC.Persistence.Outbox;

/// <summary>
/// Operational bounds for reliable outbox delivery. Gameplay outcomes never depend on these values.
/// </summary>
[Prototype("ncPersistentOutboxPolicy")]
public sealed partial class NCPersistentOutboxPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = string.Empty;

    [DataField]
    public int FetchBatchSize { get; private set; } = 16;

    [DataField]
    public int MaximumConcurrentDeliveries { get; private set; } = 8;

    [DataField]
    public int MaximumAttempts { get; private set; } = 8;

    [DataField]
    public TimeSpan PollInterval { get; private set; } = TimeSpan.FromMilliseconds(250);

    [DataField]
    public TimeSpan InitialRetryDelay { get; private set; } = TimeSpan.FromSeconds(1);

    [DataField]
    public TimeSpan MaximumRetryDelay { get; private set; } = TimeSpan.FromMinutes(5);

    [DataField]
    public TimeSpan DeliveryTimeout { get; private set; } = TimeSpan.FromSeconds(15);

    [DataField]
    public float RetryMultiplier { get; private set; } = 2f;
}
