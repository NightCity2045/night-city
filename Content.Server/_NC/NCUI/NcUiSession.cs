// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Shared._NC.NCUI;

namespace Content.Server._NC.NCUI;

public sealed record NcUiRateLimitPolicy(float TokensPerSecond, float Burst);

public sealed record NcUiSessionOptions(
    int MaximumParticipants,
    int MaximumPayloadUnits,
    int PatchHistoryLength,
    TimeSpan DefaultLeaseDuration,
    NcUiRateLimitPolicy ActionRate,
    NcUiRateLimitPolicy ContinuousRate,
    NcUiRateLimitPolicy ResyncRate)
{
    public TimeSpan MaximumLeaseDuration { get; init; } = TimeSpan.FromSeconds(10);

    public static NcUiSessionOptions Default => new(
        8,
        4096,
        128,
        TimeSpan.FromSeconds(2),
        new NcUiRateLimitPolicy(20f, 30f),
        new NcUiRateLimitPolicy(20f, 24f),
        new NcUiRateLimitPolicy(0.5f, 2f));

    public NcUiRateLimitPolicy GetRate(NcUiRateLimitChannel channel) => channel switch
    {
        NcUiRateLimitChannel.Continuous => ContinuousRate,
        NcUiRateLimitChannel.Resync => ResyncRate,
        _ => ActionRate,
    };

    public void Validate()
    {
        if (MaximumParticipants <= 0 || MaximumPayloadUnits <= 0 || PatchHistoryLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(NcUiSessionOptions), "NCUI limits must be positive.");
        if (DefaultLeaseDuration <= TimeSpan.Zero || MaximumLeaseDuration < DefaultLeaseDuration)
            throw new ArgumentOutOfRangeException(nameof(DefaultLeaseDuration));
        ValidateRate(ActionRate);
        ValidateRate(ContinuousRate);
        ValidateRate(ResyncRate);
    }

    private static void ValidateRate(NcUiRateLimitPolicy policy)
    {
        if (!float.IsFinite(policy.TokensPerSecond) || !float.IsFinite(policy.Burst) ||
            policy.TokensPerSecond <= 0f || policy.Burst <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "NCUI token-bucket values must be finite and positive.");
        }
    }
}

public sealed class NcUiServerParticipant
{
    public EntityUid Actor { get; }
    public NcUiPermission Permissions { get; set; }
    public NcUiActionSequence LastSequence { get; set; }
    public HashSet<NcUiObjectId> RemoteSelection { get; } = [];

    public NcUiServerParticipant(EntityUid actor, NcUiPermission permissions)
    {
        Actor = actor;
        Permissions = permissions;
    }
}

public sealed class NcUiServerLease
{
    public NcUiObjectId Object { get; }
    public EntityUid Owner { get; }
    public NcUiOperationId Operation { get; }
    public TimeSpan ExpiresAt { get; set; }

    public NcUiServerLease(NcUiObjectId @object, EntityUid owner, NcUiOperationId operation, TimeSpan expiresAt)
    {
        Object = @object;
        Owner = owner;
        Operation = operation;
        ExpiresAt = expiresAt;
    }
}

public sealed class NcUiServerOperation
{
    public NcUiOperationId Id { get; }
    public EntityUid Actor { get; }
    public uint LastSegmentSequence { get; set; }
    public Vector2 LastPoint { get; set; }
    public float TotalLength { get; set; }
    public TimeSpan LastClientTime { get; set; }

    public NcUiServerOperation(NcUiOperationId id, EntityUid actor, Vector2 firstPoint, TimeSpan clientTime)
    {
        Id = id;
        Actor = actor;
        LastPoint = firstPoint;
        LastClientTime = clientTime;
    }
}

/// <summary>
/// Authoritative framework state. Domain state remains owned by the gameplay system that created the session.
/// </summary>
public sealed class NcUiSession
{
    public NcUiSessionId Id { get; }
    public EntityUid Owner { get; }
    public Enum UiKey { get; }
    public NcUiRevision Revision { get; internal set; } = NcUiRevision.Initial;
    public NcUiSessionOptions Options { get; }
    public Dictionary<EntityUid, NcUiServerParticipant> Participants { get; } = [];
    public Dictionary<NcUiObjectId, NcUiServerLease> Leases { get; } = [];
    public Dictionary<NcUiOperationId, NcUiServerOperation> ActiveOperations { get; } = [];

    public NcUiSession(NcUiSessionId id, EntityUid owner, Enum uiKey, NcUiSessionOptions options)
    {
        Id = id;
        Owner = owner;
        UiKey = uiKey;
        Options = options;
    }
}
