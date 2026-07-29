// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using System.Collections.Generic;

namespace Content.Server.Database;

/// <summary>
/// Detached, read-only aggregate loaded for one persistent character.
/// Runtime ECS components cache this data but never become its source of truth.
/// </summary>
public sealed record NCCharacterSnapshot(
    string CharacterName,
    NCCharacterProgression Progression,
    IReadOnlyList<NCCharacterSkill> Skills,
    NCCharacterEmployment? Employment,
    NCBankAccount? PersonalBankAccount,
    IReadOnlyList<NCPropertyHolding> Properties,
    IReadOnlyList<NCBusinessHolding> Businesses,
    IReadOnlyList<NCCharacterLicense> Licenses,
    IReadOnlyList<NCCharacterDocument> Documents,
    NCCharacterLifecycle Lifecycle);

public sealed record NCPropertyHolding(
    NCProperty Property,
    NCPropertyOwnership Ownership);

public sealed record NCBusinessHolding(
    NCBusiness Business,
    NCBusinessOwnership Ownership,
    int CoownerCount);
