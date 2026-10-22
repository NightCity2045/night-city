// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace NightCity.Diagnostics.Contracts;

public static class DiagnosticProtocol
{
    public const int Version = 1;
    public const string HttpPath = "/nc-diagnostics/v1/request";
    public const int MaximumRequestBytes = 64 * 1024;
    public const int MaximumResponseBytes = 2 * 1024 * 1024;
    public const int MaximumQueueLength = 16;
    public const int MaximumPageSize = 100;
    public const string TokenEnvironment = "NC_DIAGNOSTICS_TOKEN";
    public const string ProfileEnvironment = "NC_DIAGNOSTICS_PROFILE";
    public const string PipeEnvironment = "NC_DIAGNOSTICS_PIPE";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static DiagnosticProfile ParseProfile(string? value) => value switch
    {
        "playtest" => DiagnosticProfile.Playtest,
        "fixture-admin" => DiagnosticProfile.FixtureAdmin,
        _ => DiagnosticProfile.Observe,
    };
}

public enum DiagnosticProfile : byte
{
    Observe,
    Playtest,
    FixtureAdmin,
}

public sealed record DiagnosticRequest
{
    public int ApiVersion { get; init; } = DiagnosticProtocol.Version;
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public required string Command { get; init; }
    public JsonObject Arguments { get; init; } = [];
    public string? ServerInstanceId { get; init; }
    public long? WorldGeneration { get; init; }
    public string? LeaseOwner { get; init; }
    public string? LeaseToken { get; init; }
    public long DeadlineUnixMilliseconds { get; init; } = DateTimeOffset.UtcNow.AddSeconds(15).ToUnixTimeMilliseconds();
}

public sealed record DiagnosticFault(string Code, string Message, bool Retryable = false);

public sealed record DiagnosticResponse
{
    public int ApiVersion { get; init; } = DiagnosticProtocol.Version;
    public required string RequestId { get; init; }
    public required string ServerInstanceId { get; init; }
    public long WorldGeneration { get; init; }
    public long CapturedTick { get; init; }
    public required string Source { get; init; }
    public JsonNode? Data { get; init; }
    public DiagnosticFault? Error { get; init; }
    public bool Truncated { get; init; }
    public string[] Warnings { get; init; } = [];
}

public sealed record DiagnosticEntityReference(string ServerInstanceId, long WorldGeneration, string NetEntity);

public sealed class DiagnosticException(string code, string message, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public static class DiagnosticArguments
{
    public static string RequiredString(this JsonObject args, string name, int maximumLength = 256)
    {
        var value = args[name] is JsonValue node && node.TryGetValue<string>(out var text) ? text : null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            throw new DiagnosticException("invalid_arguments", $"'{name}' must contain 1..{maximumLength} characters.");
        return value;
    }

    public static int BoundedInt(this JsonObject args, string name, int defaultValue, int minimum, int maximum)
    {
        var value = defaultValue;
        if (args[name] != null && (args[name] is not JsonValue node || !node.TryGetValue(out value)))
            throw new DiagnosticException("invalid_arguments", $"'{name}' must be an integer.");
        if (value < minimum || value > maximum)
            throw new DiagnosticException("invalid_arguments", $"'{name}' must be between {minimum} and {maximum}.");
        return value;
    }

    public static float BoundedFloat(this JsonObject args, string name, float defaultValue, float minimum, float maximum)
    {
        var value = defaultValue;
        if (args[name] != null && (args[name] is not JsonValue node || !node.TryGetValue(out value)))
            throw new DiagnosticException("invalid_arguments", $"'{name}' must be a number.");
        if (!float.IsFinite(value) || value < minimum || value > maximum)
            throw new DiagnosticException("invalid_arguments", $"'{name}' must be finite and between {minimum} and {maximum}.");
        return value;
    }
}
