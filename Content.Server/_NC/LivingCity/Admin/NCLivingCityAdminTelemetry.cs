// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.LivingCity.Admin;
using System.Threading;

namespace Content.Server._NC.LivingCity.Admin;

public sealed record NCLivingCityAdminTelemetrySnapshot(
    long Responses,
    long SuccessfulResponses,
    long RejectedResponses,
    long ErrorResponses,
    long ApproximatePayloadBytes);

/// <summary>
/// Process-wide counters for the bounded Living City F7 network boundary.
/// </summary>
public static class NCLivingCityAdminTelemetry
{
    private static long _responses;
    private static long _successful;
    private static long _rejected;
    private static long _errors;
    private static long _payloadBytes;

    public static void Record(bool success, string? error, IReadOnlyList<NCLivingCityAdminField> fields)
    {
        Interlocked.Increment(ref _responses);
        if (success)
            Interlocked.Increment(ref _successful);
        else
            Interlocked.Increment(ref _rejected);
        if (!string.IsNullOrWhiteSpace(error))
            Interlocked.Increment(ref _errors);

        long bytes = error?.Length ?? 0;
        foreach (var field in fields)
            bytes += field.Key.Length + field.Value.Length;
        Interlocked.Add(ref _payloadBytes, bytes * sizeof(char));
    }

    public static NCLivingCityAdminTelemetrySnapshot Snapshot() => new(
        Interlocked.Read(ref _responses),
        Interlocked.Read(ref _successful),
        Interlocked.Read(ref _rejected),
        Interlocked.Read(ref _errors),
        Interlocked.Read(ref _payloadBytes));
}
