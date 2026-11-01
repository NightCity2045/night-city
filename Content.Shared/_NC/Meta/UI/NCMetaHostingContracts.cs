// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Meta.UI;

[Prototype("ncMetaHosting")]
public sealed partial class NCMetaHostingProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = "";
    [DataField] public long DomainFee = 100;
    [DataField] public long HostingUnitFee = 50;
    [DataField] public int TermSeconds = 3600;
    [DataField] public int QuoteSeconds = 30;
    [DataField] public int CapacityUnits = 16;
    [DataField] public int SessionsPerUnit = 4;
    [DataField] public int DefaultGraceSeconds = 60;
}

[Serializable, NetSerializable]
public sealed record NCMetaHostingQuoteSummary(Guid QuoteId, string Domain, string HostDeviceId, string View,
    long DomainFee, long HostingFee, int Units, int TermSeconds, long ExpiresAtTicks);

[Serializable, NetSerializable]
public sealed record NCMetaHostingLeaseSummary(Guid LeaseId, string Domain, string HostDeviceId, string View,
    int Units, long ExpiresAtTicks, string Status);
