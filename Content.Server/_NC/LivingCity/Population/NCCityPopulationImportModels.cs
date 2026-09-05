// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.LivingCity.Population;

public enum NCCityPopulationImportStatus : byte
{
    Completed,
    Rejected,
    AlreadyRunning,
    Cancelled,
}

public sealed record NCCityPopulationImportFailure(
    string StableLocalId,
    NCActorId ActorId,
    string Error);

public sealed record NCCityPopulationImportReport(
    NCCityPopulationImportStatus Status,
    string ProfileId,
    int Requested,
    int Created,
    int Existing,
    int Conflicts,
    int Failed,
    IReadOnlyList<NCCityPopulationImportFailure> Failures);

public sealed record NCCityPopulationImportProgress(
    bool Running,
    string? ProfileId,
    int Requested,
    int Processed,
    int Created,
    int Existing,
    int Conflicts,
    int Failed);
