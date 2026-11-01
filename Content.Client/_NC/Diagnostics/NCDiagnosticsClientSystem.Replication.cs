// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#if TOOLS && NC_DIAGNOSTICS
using System.Linq;
using System.Text.Json.Nodes;
using Content.Shared.Doors.Components;
using Robust.Client.GameStates;
using NightCity.Diagnostics.Contracts;
using Robust.Shared.GameStates;

namespace Content.Client._NC.Diagnostics;

public sealed partial class NCDiagnosticsClientSystem
{
    private readonly Dictionary<EntityUid, TimeSpan> _replicationWatches = new();
    private readonly Dictionary<EntityUid, Queue<JsonObject>> _doorHistory = new();

    private JsonObject WatchReplication(JsonObject args)
    {
        var uid = ResolveEntity(args);
        if (!HasComp<DoorComponent>(uid))
            throw new DiagnosticException("unsupported_adapter", "Authoritative state history currently supports the Door adapter.");
        foreach (var expired in _replicationWatches.Where(x => x.Value <= _timing.RealTime || Deleted(x.Key)).Select(x => x.Key).ToArray())
        {
            _replicationWatches.Remove(expired);
            _doorHistory.Remove(expired);
        }
        if (!_replicationWatches.ContainsKey(uid) && _replicationWatches.Count >= 16)
            throw new DiagnosticException("watch_limit", "At most 16 entities may have replication watches.");
        _replicationWatches[uid] = _timing.RealTime + TimeSpan.FromSeconds(args.BoundedInt("durationSeconds", 30, 1, 60));
        _doorHistory.TryAdd(uid, new Queue<JsonObject>());
        return new JsonObject { ["watching"] = true, ["netEntity"] = GetNetEntity(uid).ToString(), ["adapter"] = "Door",
            ["historyStartsWithNextServerState"] = true };
    }

    private JsonObject UnwatchReplication(JsonObject args)
    {
        var uid = ResolveEntity(args);
        _replicationWatches.Remove(uid);
        _doorHistory.Remove(uid);
        return new JsonObject { ["watching"] = false };
    }

    private void OnGameStateApplied(GameStateAppliedArgs args)
    {
        // This callback runs after prediction was reset and the complete server state was applied,
        // before future ticks are predicted. AutoHandleState also fires during rollback and is insufficient here.
        foreach (var (uid, until) in _replicationWatches)
        {
            if (until <= _timing.RealTime || !TryComp<DoorComponent>(uid, out var door) ||
                !TryComp(uid, out MetaDataComponent? metadata) || (metadata.Flags & MetaDataFlags.Detached) != 0)
                continue;
            var history = _doorHistory[uid];
            var state = door.State;
            history.Enqueue(new JsonObject { ["appliedTick"] = args.AppliedState.ToSequence.Value, ["frame"] = _frame,
                ["State"] = state.ToString(), ["Partial"] = door.Partial,
                ["representation"] = "after-server-game-state-before-prediction" });
            while (history.Count > 16)
                history.Dequeue();
        }
    }
}
#endif
