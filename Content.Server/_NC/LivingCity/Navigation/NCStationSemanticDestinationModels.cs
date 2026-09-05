// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.LivingCity.Navigation;

/// <summary>
/// Subjective station destination categories exposed to the neural context. The destination handle is the only
/// value accepted back from the model; runtime entities and coordinates never cross that boundary.
/// </summary>
public enum NCStationSemanticDestinationKind : byte
{
    Workplace,
    Department,
    Landmark,
}

/// <summary>One bounded, request-scoped station destination safe to serialize into a neural request.</summary>
public readonly record struct NCStationSemanticDestination(
    string Handle,
    string Label,
    NCStationSemanticDestinationKind Kind,
    bool AssignedJob,
    float DistanceMeters)
{
    /// <summary>Compact semantic category sent to GPT without exposing station implementation details.</summary>
    public string ContextKind => AssignedJob && Kind == NCStationSemanticDestinationKind.Workplace
        ? "assigned-workplace"
        : Kind switch
        {
            NCStationSemanticDestinationKind.Workplace => "workplace",
            NCStationSemanticDestinationKind.Department => "department",
            NCStationSemanticDestinationKind.Landmark => "landmark",
            _ => "location",
        };
}
