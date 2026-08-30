// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.NCUI;
using Robust.Shared.Timing;

namespace Content.Server._NC.NCUI;

/// <summary>
/// Per-participant token buckets split by traffic class so strokes cannot starve discrete actions.
/// </summary>
public sealed partial class NcUiRateLimitSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private NcUiSessionSystem _sessions = default!;

    private readonly Dictionary<(EntityUid Actor, NcUiSessionId Session, NcUiRateLimitChannel Channel), Bucket> _buckets = [];

    public override void Initialize()
    {
        base.Initialize();
        _sessions.ParticipantLeft += OnParticipantLeft;
        _sessions.SessionClosed += OnSessionClosed;
    }

    public override void Shutdown()
    {
        _sessions.ParticipantLeft -= OnParticipantLeft;
        _sessions.SessionClosed -= OnSessionClosed;
        _buckets.Clear();
        base.Shutdown();
    }

    public bool TryConsume(
        EntityUid actor,
        NcUiSessionId session,
        NcUiRateLimitChannel channel,
        NcUiRateLimitPolicy policy,
        float cost = 1f)
    {
        if (cost <= 0f)
            return true;
        if (!float.IsFinite(cost) || !float.IsFinite(policy.TokensPerSecond) ||
            !float.IsFinite(policy.Burst) || policy.TokensPerSecond <= 0f || policy.Burst <= 0f)
        {
            return false;
        }
        var key = (actor, session, channel);
        var now = _timing.CurTime;
        if (!_buckets.TryGetValue(key, out var bucket))
            bucket = new Bucket(policy.Burst, now);
        var elapsed = Math.Max(0d, (now - bucket.UpdatedAt).TotalSeconds);
        bucket.Tokens = Math.Min(policy.Burst, bucket.Tokens + (float)elapsed * policy.TokensPerSecond);
        bucket.UpdatedAt = now;
        if (bucket.Tokens < cost)
        {
            _buckets[key] = bucket;
            return false;
        }
        bucket.Tokens -= cost;
        _buckets[key] = bucket;
        return true;
    }

    public void Clear(EntityUid actor, NcUiSessionId session)
    {
        foreach (var channel in Enum.GetValues<NcUiRateLimitChannel>())
            _buckets.Remove((actor, session, channel));
    }

    private void OnParticipantLeft(NcUiSession session, EntityUid actor) => Clear(actor, session.Id);

    private void OnSessionClosed(NcUiSession session)
    {
        foreach (var actor in session.Participants.Keys)
            Clear(actor, session.Id);
    }

    private struct Bucket
    {
        public float Tokens;
        public TimeSpan UpdatedAt;

        public Bucket(float tokens, TimeSpan updatedAt)
        {
            Tokens = tokens;
            UpdatedAt = updatedAt;
        }
    }
}
