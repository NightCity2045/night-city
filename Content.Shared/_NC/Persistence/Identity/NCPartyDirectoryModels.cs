// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Persistence.Identity;

[Serializable, NetSerializable]
public enum NCPartyLifecycle : byte
{
    Active,
    Deceased,
    Dissolved,
    Archived,
}

/// <summary>
/// Persistent metadata for parties that do not already have a character or city-actor aggregate.
/// It contains no credentials, private biography, or runtime entity reference.
/// </summary>
public sealed record NCPartyDirectorySnapshot(
    NCPartyRef Party,
    string DisplayName,
    NCPartyLifecycle Lifecycle,
    NCActiveWorldTime RegisteredAt,
    NCActiveWorldTime ChangedAt,
    int DataVersion,
    Guid LastAppliedEventId);

