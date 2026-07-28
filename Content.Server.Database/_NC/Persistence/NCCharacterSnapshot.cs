using System.Collections.Generic;

namespace Content.Server.Database;

/// <summary>
/// Detached, read-only aggregate loaded for one persistent character.
/// Runtime ECS components cache this data but never become its source of truth.
/// </summary>
public sealed record NCCharacterSnapshot(
    NCCharacterProgression Progression,
    IReadOnlyList<NCCharacterSkill> Skills,
    NCCharacterEmployment? Employment,
    NCBankAccount? PersonalBankAccount,
    IReadOnlyList<NCPropertyOwnership> Properties,
    IReadOnlyList<NCBusinessOwnership> Businesses,
    IReadOnlyList<NCCharacterLicense> Licenses,
    NCCharacterLifecycle Lifecycle);
