// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.LivingCity.Lifecycle;

[Serializable, NetSerializable]
public enum NCEstateStatus : byte { Open, Claims, DistributionReady, Distributed, Closed, Contested }
[Serializable, NetSerializable]
public enum NCEstateAssetKind : byte { Money, Item, Commodity, Property, Business, Contract, Obligation }
[Serializable, NetSerializable]
public enum NCEstateClaimStatus : byte { Filed, Accepted, Rejected, Paid }

public sealed record NCEstateAssetRef(NCEstateAssetKind Kind, string AssetId, long Value, bool Transferred);
public sealed record NCEstateClaim(Guid ClaimId, NCPartyRef Claimant, long Amount, string ReasonKey,
    NCEstateClaimStatus Status, NCActiveWorldTime FiledAt);
public sealed record NCEstateSnapshot(NCEstateId EstateId, NCPartyRef EstateParty, NCPartyRef Decedent,
    NCPartyRef Administrator, NCPartyRef Beneficiary, NCPartyRef Replacement, NCEstateStatus Status, Guid DeathEventId,
    IReadOnlyList<NCEstateAssetRef> Assets, IReadOnlyList<NCEstateClaim> Claims, NCActiveWorldTime OpenedAt,
    NCActiveWorldTime? ClosedAt, int DataVersion, Guid LastAppliedEventId);
