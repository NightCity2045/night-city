// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Globalization;
using System.Text.Json;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.LivingCity.Neural;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.LivingCity.Neural;

public sealed record NCNeuralUsageSnapshot(
    Guid RequestId,
    NCActorId ActorId,
    NCSessionEpoch SessionEpoch,
    NCNeuralRequestKind Kind,
    string PromptProfileId,
    string ProviderModel,
    string ProviderRequestId,
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    long InputCostUsdMicros,
    long CachedInputCostUsdMicros,
    long OutputCostUsdMicros,
    long TotalCostUsdMicros,
    NCActiveWorldTime RecordedAt,
    int DataVersion,
    Guid LastAppliedEventId,
    int SchemaVersion = 0,
    string PromptStableKey = "",
    int PromptVersion = 0,
    string EvaluationProfile = "");

public sealed record NCNeuralUsageRecord(
    NCNeuralUsageSnapshot Snapshot,
    long AggregateVersion,
    NCActiveWorldTime PersistedAt);

public sealed record NCNeuralUsageSessionSnapshot(
    NCSessionEpoch SessionEpoch,
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    long TotalCostUsdMicros,
    long RecordedRequests,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCNeuralUsageSessionRecord(
    NCNeuralUsageSessionSnapshot Snapshot,
    long AggregateVersion,
    NCActiveWorldTime PersistedAt);

public sealed record NCNeuralUsageWeekSnapshot(
    string PeriodKey,
    long TotalCostUsdMicros,
    long RecordedRequests,
    Dictionary<string, long> ActorCostUsdMicros,
    Dictionary<string, long> KindCostUsdMicros,
    Dictionary<string, long> KindRecordedRequests,
    NCActiveWorldTime UpdatedAt,
    int DataVersion,
    Guid LastAppliedEventId);

public sealed record NCNeuralUsageWeekRecord(
    NCNeuralUsageWeekSnapshot Snapshot,
    long AggregateVersion,
    NCActiveWorldTime PersistedAt);

/// <summary>
/// Immutable external-provider usage telemetry. It never authorizes or denies cognition: provider availability and
/// balance remain the spending authority. One request owns one aggregate, making accounting retries idempotent.
/// No prompt, dialogue, memory, or provider credential is written to persistence.
/// </summary>
public sealed partial class NCNeuralUsageLedgerSystem : EntitySystem
{
    public const string AggregateType = "living-city.neural-usage";
    public const string SessionAggregateType = "living-city.neural-usage-session";
    public const string WeekAggregateType = "living-city.neural-usage-week";
    public const int SnapshotFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Dependency] private IServerDbManager _database = default!;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<NCNeuralUsageRecord?> GetAsync(Guid requestId)
    {
        if (requestId == Guid.Empty)
            return null;

        var stored = await _database.GetNCAggregateSnapshotAsync(AggregateType, RequestKey(requestId));
        return Deserialize(stored, requestId);
    }

    public async Task<NCNeuralUsageSessionRecord?> GetSessionAsync(NCSessionEpoch sessionEpoch)
    {
        if (!sessionEpoch.IsValid)
            return null;

        var stored = await _database.GetNCAggregateSnapshotAsync(
            SessionAggregateType,
            SessionKey(sessionEpoch));
        return DeserializeSession(stored, sessionEpoch);
    }

    public async Task<NCNeuralUsageWeekRecord?> GetWeekAsync(string periodKey)
    {
        if (!ValidPeriodKey(periodKey))
            return null;

        var stored = await _database.GetNCAggregateSnapshotAsync(WeekAggregateType, periodKey);
        return DeserializeWeek(stored, periodKey);
    }

    public async Task<NCNeuralUsageRecord?> RecordAsync(
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope response,
        NCNeuralPolicyPrototype policy,
        NCActiveWorldTime now)
    {
        if (!Valid(request, response, policy, now))
            return null;

        await _gate.WaitAsync();
        try
        {
            if (await GetAsync(request.RequestId) is { } existing)
                return existing;

            var currentSession = await GetSessionAsync(request.SessionEpoch);
            var periodKey = CurrentWeekKey(DateTimeOffset.UtcNow);
            var currentWeek = await GetWeekAsync(periodKey);
            var uncachedInput = response.InputTokens - response.CachedInputTokens;
            var inputCost = CalculateCost(uncachedInput, policy.InputPriceUsdMicrosPerMillionTokens);
            var cachedInputCost = CalculateCost(
                response.CachedInputTokens,
                policy.CachedInputPriceUsdMicrosPerMillionTokens);
            var outputCost = CalculateCost(response.OutputTokens, policy.OutputPriceUsdMicrosPerMillionTokens);
            var totalCost = checked(inputCost + cachedInputCost + outputCost);
            var eventId = Guid.NewGuid();
            var sessionEventId = Guid.NewGuid();
            var snapshot = new NCNeuralUsageSnapshot(
                request.RequestId,
                request.ActorId,
                request.SessionEpoch,
                request.Kind,
                request.PromptProfile.Id,
                response.ProviderModel,
                response.ProviderRequestId,
                response.InputTokens,
                response.CachedInputTokens,
                response.OutputTokens,
                inputCost,
                cachedInputCost,
                outputCost,
                totalCost,
                now,
                2,
                eventId,
                response.SchemaVersion,
                response.PromptStableKey,
                response.PromptVersion,
                response.EvaluationProfile);
            var current = currentSession?.Snapshot;
            var sessionSnapshot = new NCNeuralUsageSessionSnapshot(
                request.SessionEpoch,
                checked((current?.InputTokens ?? 0) + response.InputTokens),
                checked((current?.CachedInputTokens ?? 0) + response.CachedInputTokens),
                checked((current?.OutputTokens ?? 0) + response.OutputTokens),
                checked((current?.TotalCostUsdMicros ?? 0) + totalCost),
                checked((current?.RecordedRequests ?? 0) + 1),
                now,
                checked((current?.DataVersion ?? 0) + 1),
                sessionEventId);
            var currentWeekSnapshot = currentWeek?.Snapshot;
            var actorCosts = currentWeekSnapshot == null
                ? []
                : new Dictionary<string, long>(currentWeekSnapshot.ActorCostUsdMicros);
            var kindCosts = currentWeekSnapshot == null
                ? []
                : new Dictionary<string, long>(currentWeekSnapshot.KindCostUsdMicros);
            var kindRequests = currentWeekSnapshot == null
                ? []
                : new Dictionary<string, long>(currentWeekSnapshot.KindRecordedRequests);
            var actorKey = request.ActorId.Value.ToString("D");
            var kindKey = request.Kind.ToString();
            actorCosts[actorKey] = checked(actorCosts.GetValueOrDefault(actorKey) + totalCost);
            kindCosts[kindKey] = checked(kindCosts.GetValueOrDefault(kindKey) + totalCost);
            kindRequests[kindKey] = checked(kindRequests.GetValueOrDefault(kindKey) + 1);
            var weekEventId = Guid.NewGuid();
            var weekSnapshot = new NCNeuralUsageWeekSnapshot(
                periodKey,
                checked((currentWeekSnapshot?.TotalCostUsdMicros ?? 0) + totalCost),
                checked((currentWeekSnapshot?.RecordedRequests ?? 0) + 1),
                actorCosts,
                kindCosts,
                kindRequests,
                now,
                checked((currentWeekSnapshot?.DataVersion ?? 0) + 1),
                weekEventId);

            var key = RequestKey(request.RequestId);
            var sessionKey = SessionKey(request.SessionEpoch);
            var result = await _database.ApplyNCPersistentBatchMutationAsync(new NCPersistentBatchMutationRequest(
                NCPersistentIdentity.Command("neural-usage", "record", key),
                "living-city.neural-usage.record",
                "neural-request",
                key,
                [
                    new NCPersistentAggregateWrite(
                        eventId,
                        "living-city.neural-usage.recorded",
                        AggregateType,
                        key,
                        0,
                        SnapshotFormatVersion,
                        JsonSerializer.Serialize(snapshot, JsonOptions),
                        JsonSerializer.Serialize(new UsageRecordedEvent(
                            request.RequestId,
                            request.ActorId,
                            response.ProviderModel,
                            response.SchemaVersion,
                            response.PromptStableKey,
                            response.PromptVersion,
                            response.EvaluationProfile,
                            totalCost), JsonOptions)),
                    new NCPersistentAggregateWrite(
                        sessionEventId,
                        "living-city.neural-usage-session.updated",
                        SessionAggregateType,
                        sessionKey,
                        currentSession?.AggregateVersion ?? 0,
                        SnapshotFormatVersion,
                        JsonSerializer.Serialize(sessionSnapshot, JsonOptions),
                        JsonSerializer.Serialize(new UsageSessionUpdatedEvent(
                            request.SessionEpoch,
                            request.RequestId,
                            totalCost), JsonOptions)),
                    new NCPersistentAggregateWrite(
                        weekEventId,
                        "living-city.neural-usage-week.updated",
                        WeekAggregateType,
                        periodKey,
                        currentWeek?.AggregateVersion ?? 0,
                        SnapshotFormatVersion,
                        JsonSerializer.Serialize(weekSnapshot, JsonOptions),
                        JsonSerializer.Serialize(new UsageWeekUpdatedEvent(
                            periodKey,
                            request.RequestId,
                            request.ActorId,
                            request.Kind,
                            totalCost), JsonOptions)),
                ],
                JsonSerializer.Serialize(request.RequestId, JsonOptions),
                NCPersistentIdentity.Correlation("neural-usage", "record", key),
                null,
                request.SessionEpoch,
                now,
                []));

            if (result.Status is not (NCPersistentMutationStatus.Applied or
                NCPersistentMutationStatus.DuplicateCompleted))
            {
                return null;
            }

            return await GetAsync(request.RequestId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static long CalculateCost(int tokens, long priceUsdMicrosPerMillionTokens)
    {
        if (tokens <= 0 || priceUsdMicrosPerMillionTokens <= 0)
            return 0;

        return checked((long) Math.Ceiling(
            tokens * (decimal) priceUsdMicrosPerMillionTokens / 1_000_000m));
    }

    private static NCNeuralUsageRecord? Deserialize(NCAggregateSnapshotData? stored, Guid expectedRequestId)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion || stored.AggregateVersion != 1)
            return null;

        try
        {
            var snapshot = JsonSerializer.Deserialize<NCNeuralUsageSnapshot>(stored.Payload, JsonOptions);
            return snapshot?.RequestId == expectedRequestId && Valid(snapshot)
                ? new NCNeuralUsageRecord(snapshot, stored.AggregateVersion, stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NCNeuralUsageSessionRecord? DeserializeSession(
        NCAggregateSnapshotData? stored,
        NCSessionEpoch expectedSessionEpoch)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion || stored.AggregateVersion < 1)
            return null;

        try
        {
            var snapshot = JsonSerializer.Deserialize<NCNeuralUsageSessionSnapshot>(stored.Payload, JsonOptions);
            return snapshot?.SessionEpoch == expectedSessionEpoch && Valid(snapshot)
                ? new NCNeuralUsageSessionRecord(snapshot, stored.AggregateVersion, stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NCNeuralUsageWeekRecord? DeserializeWeek(
        NCAggregateSnapshotData? stored,
        string expectedPeriodKey)
    {
        if (stored == null || stored.FormatVersion != SnapshotFormatVersion || stored.AggregateVersion < 1)
            return null;

        try
        {
            var snapshot = JsonSerializer.Deserialize<NCNeuralUsageWeekSnapshot>(stored.Payload, JsonOptions);
            return snapshot?.PeriodKey == expectedPeriodKey && Valid(snapshot)
                ? new NCNeuralUsageWeekRecord(snapshot, stored.AggregateVersion, stored.ActiveWorldTime)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Valid(
        NCNeuralRequestEnvelope request,
        NCNeuralResponseEnvelope response,
        NCNeuralPolicyPrototype policy,
        NCActiveWorldTime now) =>
        request.RequestId != Guid.Empty && request.ActorId.IsValid && request.SessionEpoch.IsValid && now.IsValid &&
        response.RequestId == request.RequestId && response.InputTokens >= 0 && response.OutputTokens >= 0 &&
        response.CachedInputTokens >= 0 && response.CachedInputTokens <= response.InputTokens &&
        response.ProviderModel == policy.ProviderModel && response.ProviderRequestId.Length is > 0 and <= 128 &&
        response.SchemaVersion > 0 && Stable(response.PromptStableKey) && response.PromptVersion > 0 &&
        Stable(response.EvaluationProfile);

    private static bool Valid(NCNeuralUsageSnapshot value) =>
        value.RequestId != Guid.Empty && value.ActorId.IsValid && value.SessionEpoch.IsValid &&
        value.RecordedAt.IsValid && value.InputTokens >= 0 && value.OutputTokens >= 0 &&
        value.CachedInputTokens >= 0 && value.CachedInputTokens <= value.InputTokens &&
        value.InputCostUsdMicros >= 0 && value.CachedInputCostUsdMicros >= 0 && value.OutputCostUsdMicros >= 0 &&
        value.TotalCostUsdMicros == value.InputCostUsdMicros + value.CachedInputCostUsdMicros +
            value.OutputCostUsdMicros &&
        value.LastAppliedEventId != Guid.Empty &&
        (value.DataVersion == 1 && value.SchemaVersion == 0 && string.IsNullOrEmpty(value.PromptStableKey) &&
         value.PromptVersion == 0 && string.IsNullOrEmpty(value.EvaluationProfile) ||
         value.DataVersion == 2 && value.SchemaVersion > 0 && Stable(value.PromptStableKey) &&
         value.PromptVersion > 0 && Stable(value.EvaluationProfile));

    private static bool Valid(NCNeuralUsageSessionSnapshot value) =>
        value.SessionEpoch.IsValid && value.InputTokens >= 0 && value.CachedInputTokens >= 0 &&
        value.CachedInputTokens <= value.InputTokens && value.OutputTokens >= 0 &&
        value.TotalCostUsdMicros >= 0 && value.RecordedRequests > 0 && value.UpdatedAt.IsValid &&
        value.DataVersion > 0 && value.LastAppliedEventId != Guid.Empty;

    private static bool Valid(NCNeuralUsageWeekSnapshot value) =>
        ValidPeriodKey(value.PeriodKey) && value.TotalCostUsdMicros >= 0 && value.RecordedRequests > 0 &&
        value.ActorCostUsdMicros.Count <= 4096 && value.KindCostUsdMicros.Count <= 32 &&
        value.KindRecordedRequests.Count <= 32 && value.ActorCostUsdMicros.Values.All(cost => cost >= 0) &&
        value.KindCostUsdMicros.Values.All(cost => cost >= 0) &&
        value.KindRecordedRequests.Values.All(count => count > 0) && value.UpdatedAt.IsValid &&
        value.DataVersion > 0 && value.LastAppliedEventId != Guid.Empty;

    private static string RequestKey(Guid requestId) => requestId.ToString("D");
    private static string SessionKey(NCSessionEpoch sessionEpoch) => sessionEpoch.Value.ToString("D");

    public static string CurrentWeekKey(DateTimeOffset now)
    {
        var utc = now.UtcDateTime;
        return string.Create(CultureInfo.InvariantCulture, $"{ISOWeek.GetYear(utc):0000}-W{ISOWeek.GetWeekOfYear(utc):00}");
    }

    private static bool ValidPeriodKey(string value) =>
        value.Length == 8 && value[4] == '-' && value[5] == 'W' &&
        int.TryParse(value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) &&
        int.TryParse(value.AsSpan(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var week) &&
        year is >= 2000 and <= 9999 && week is >= 1 and <= 53;

    private static bool Stable(string? value) => value is { Length: > 0 and <= 128 } && value.All(character =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private sealed record UsageRecordedEvent(
        Guid RequestId,
        NCActorId ActorId,
        string ProviderModel,
        int SchemaVersion,
        string PromptStableKey,
        int PromptVersion,
        string EvaluationProfile,
        long TotalCostUsdMicros);

    private sealed record UsageSessionUpdatedEvent(
        NCSessionEpoch SessionEpoch,
        Guid RequestId,
        long TotalCostUsdMicros);

    private sealed record UsageWeekUpdatedEvent(
        string PeriodKey,
        Guid RequestId,
        NCActorId ActorId,
        NCNeuralRequestKind Kind,
        long TotalCostUsdMicros);
}
