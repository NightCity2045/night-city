// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Collections.Generic;
using System.Linq;
using Robust.Shared.Network;

namespace Content.Server._NC.LivingCity.Admin;

/// <summary>
/// Bounded, round-local, single-use confirmations for causality-changing F7 operations. Tokens are bound to both the
/// authenticated administrator and a digest of every exact request field.
/// </summary>
internal sealed class NCLivingCityAdminConfirmationStore
{
    private const int MaximumConfirmations = 256;
    private readonly Dictionary<(NetUserId UserId, Guid Token), Confirmation> _confirmations = [];

    internal int Count => _confirmations.Count;

    internal Guid Issue(NetUserId userId, string fingerprint, TimeSpan now, TimeSpan lifetime)
    {
        foreach (var key in _confirmations
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _confirmations.Remove(key);
        }

        if (_confirmations.Count >= MaximumConfirmations)
            _confirmations.Clear();
        var token = Guid.NewGuid();
        _confirmations[(userId, token)] = new Confirmation(fingerprint, now + lifetime);
        return token;
    }

    internal bool Consume(NetUserId userId, Guid token, string fingerprint, TimeSpan now)
    {
        if (token == Guid.Empty || !_confirmations.Remove((userId, token), out var confirmation))
            return false;
        return confirmation.ExpiresAt > now &&
               string.Equals(confirmation.Fingerprint, fingerprint, StringComparison.Ordinal);
    }

    internal void Clear() => _confirmations.Clear();

    private sealed record Confirmation(string Fingerprint, TimeSpan ExpiresAt);
}
