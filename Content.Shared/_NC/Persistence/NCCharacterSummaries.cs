// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Robust.Shared.Serialization;
using System;

namespace Content.Shared._NC.Persistence;

[Serializable, NetSerializable]
public sealed record NCPropertySummary(
    string PrototypeId,
    string PropertyType,
    int ShareBasisPoints,
    byte Status);

[Serializable, NetSerializable]
public sealed record NCBusinessSummary(
    Guid BusinessId,
    string Name,
    string BusinessType,
    int ShareBasisPoints,
    int CoownerCount,
    byte Status);

[Serializable, NetSerializable]
public sealed record NCLegalSummary(
    string PrototypeId,
    byte Status,
    DateTime? ExpiresAt,
    string? SerialNumber);
