// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Housing.Prototypes;

/// <summary>
/// Data-driven safety bounds for one apartment snapshot.
/// </summary>
[Prototype("ncPropertySnapshotPolicy")]
public sealed partial class NCPropertySnapshotPolicyPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public int MaxRootEntities { get; private set; }

    [DataField(required: true)]
    public int MaxTotalEntities { get; private set; }

    [DataField(required: true)]
    public int MaxPayloadBytes { get; private set; }
}
