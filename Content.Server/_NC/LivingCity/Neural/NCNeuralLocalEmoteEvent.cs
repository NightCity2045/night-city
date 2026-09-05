// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.LivingCity.Neural;

/// <summary>Server-local bridge for a successfully emitted free-form emote; it carries no gameplay authority.</summary>
public sealed class NCNeuralLocalEmoteEvent(EntityUid source, string message) : EntityEventArgs
{
    public EntityUid Source { get; } = source;
    public string Message { get; } = message;
}
