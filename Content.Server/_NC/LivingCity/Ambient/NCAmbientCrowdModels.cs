// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Map;

namespace Content.Server._NC.LivingCity.Ambient;

public readonly record struct NCAmbientCrowdCounters(
    bool Active,
    int InteractiveEntities,
    int DecorativeVisuals,
    long Spawned,
    long Despawned,
    long SpawnRejected,
    long ThreatReactions,
    string? ConfigurationError);

/// <summary>
/// Raised for domain adapters when a physical ambient witness is harmed. Consumers may report a causal incident,
/// but the ambient entity itself never becomes a persistent party or stores long-term evidence.
/// </summary>
[ByRefEvent]
public readonly record struct NCAmbientWitnessedViolenceEvent(
    EntityUid Witness,
    EntityUid? Source,
    MapCoordinates Location);
