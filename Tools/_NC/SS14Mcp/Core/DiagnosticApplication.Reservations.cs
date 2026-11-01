// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

public sealed partial class DiagnosticApplication
{
    private async Task<string> AcquireLeaseAsync(DiagnosticTarget target, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(target.Id, out var session))
        {
            session = await _bridge.ExecuteAsync(target, new DiagnosticRequest { Command = "session_get" }, cancellationToken);
            CheckResponse(session);
            _sessions[target.Id] = session;
        }
        var lease = await _bridge.ExecuteAsync(target, new DiagnosticRequest
        {
            Command = "lease_acquire", ServerInstanceId = session.ServerInstanceId, WorldGeneration = session.WorldGeneration,
            Arguments = new JsonObject { ["owner"] = _leaseOwner, ["token"] = _leases.GetValueOrDefault(target.Id), ["seconds"] = 30 },
        }, cancellationToken);
        if (lease.Error?.Code == "stale_world")
        {
            _sessions.TryRemove(target.Id, out _);
            _leases.TryRemove(target.Id, out _);
        }
        CheckResponse(lease);
        var token = lease.Data?["token"]?.GetValue<string>()
            ?? throw new DiagnosticException("invalid_response", "The bridge returned no mutation lease.");
        _leases[target.Id] = token;
        return token;
    }

    internal string[] ReservationTargets(string serverTargetId)
    {
        if (!_targets.TryGetValue(serverTargetId, out var target))
            throw new DiagnosticException("target_not_found", "The scenario target is not registered.");
        return target.EnvironmentId is { } group
            ? _targets.Values.Where(item => item.EnvironmentId == group).OrderBy(item => item.Kind == "server" ? 0 : 1).Select(item => item.Id).ToArray()
            : [serverTargetId];
    }

    internal async Task RenewReservationsAsync(string[] targetIds, bool release, CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var id in targetIds)
            {
                if (!_targets.TryGetValue(id, out var target))
                    continue;
                if (!release)
                    await AcquireLeaseAsync(target, cancellationToken);
                else if (_leases.TryRemove(id, out var token) && _sessions.TryGetValue(id, out var session))
                {
                    var response = await _bridge.ExecuteAsync(target, new DiagnosticRequest
                    {
                        Command = "lease_release", ServerInstanceId = session.ServerInstanceId, WorldGeneration = session.WorldGeneration,
                        Arguments = new JsonObject { ["owner"] = _leaseOwner, ["token"] = token },
                    }, cancellationToken);
                    CheckResponse(response);
                }
            }
        }
        finally { _mutationGate.Release(); }
    }
}
