// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Meta.Hosting;

public sealed record MetaAuthorityStamp(Guid InstallationId, Guid ProcessGeneration, string Principal,
    string DeviceId, ulong DeviceEpoch, ulong AuthorityEpoch, long ExpiresAtMs);

/// <summary>Only the trusted host issues grants. A copied guest string never creates one.</summary>
public sealed class MetaGrant
{
    private readonly HashSet<string> _scopes;
    public MetaAuthorityStamp Stamp { get; }
    public bool Revoked { get; private set; }

    public MetaGrant(MetaAuthorityStamp stamp, IEnumerable<string> grantedScopes,
        IEnumerable<string> requestedScopes, IEnumerable<string> hardwareScopes)
    {
        if (stamp.InstallationId == Guid.Empty || stamp.ProcessGeneration == Guid.Empty ||
            string.IsNullOrWhiteSpace(stamp.Principal) || string.IsNullOrWhiteSpace(stamp.DeviceId))
            throw new ArgumentException("Authority requires stable identities.", nameof(stamp));
        Stamp = stamp;
        _scopes = new HashSet<string>(grantedScopes, StringComparer.Ordinal);
        _scopes.IntersectWith(requestedScopes);
        _scopes.IntersectWith(hardwareScopes);
    }

    public bool CanUse(string scope, Guid processGeneration, string deviceId, ulong deviceEpoch,
        ulong authorityEpoch, long nowMs) => !Revoked && nowMs >= 0 &&
        nowMs < Stamp.ExpiresAtMs && processGeneration == Stamp.ProcessGeneration &&
        deviceId == Stamp.DeviceId && deviceEpoch == Stamp.DeviceEpoch &&
        authorityEpoch == Stamp.AuthorityEpoch && _scopes.Contains(scope);

    public void Revoke() => Revoked = true;
}

/// <summary>Bounded token bucket on the simulation clock; regressing or wrapping time never adds credit.</summary>
public sealed class MetaRateLimit(int capacity, int perSecond)
{
    private double _tokens = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private long _lastMs;
    private readonly int _rate = perSecond > 0 ? perSecond : throw new ArgumentOutOfRangeException(nameof(perSecond));

    /// <summary>Read-only admission preview. Callers must serialize preview and consumption.</summary>
    public bool CanConsume(long nowMs, int amount = 1) => nowMs >= 0 && amount >= 1 && amount <= capacity &&
        (nowMs > _lastMs ? Math.Min(capacity, _tokens + (nowMs - _lastMs) / 1000.0 * _rate) : _tokens) >= amount;

    /// <summary>Consumes all independent hierarchy buckets or none on the authoritative thread.</summary>
    public static bool TryConsumeAll(long nowMs, int amount, ReadOnlySpan<MetaRateLimit> limits)
    {
        if (limits.Length is < 1 or > 16) return false;
        for (var index = 0; index < limits.Length; index++)
        {
            if (limits[index] is not { } limit || !limit.CanConsume(nowMs, amount)) return false;
            for (var previous = 0; previous < index; previous++)
                if (ReferenceEquals(limits[previous], limit)) return false;
        }
        foreach (var limit in limits) limit.TryConsume(nowMs, amount);
        return true;
    }

    public bool TryConsume(long nowMs, int amount = 1)
    {
        if (nowMs < 0 || amount < 1 || amount > capacity)
            return false;
        if (nowMs > _lastMs)
        {
            _tokens = Math.Min(capacity, _tokens + (nowMs - _lastMs) / 1000.0 * _rate);
            _lastMs = nowMs;
        }
        if (_tokens < amount)
            return false;
        _tokens -= amount;
        return true;
    }
}
