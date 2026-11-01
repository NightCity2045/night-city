// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server.Database;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Timing;

namespace Content.Server._NC.Meta.Persistence;

/// <summary>Serial, bounded storage housekeeping; never traverses the world or performs synchronous DB work.</summary>
public sealed partial class NCMetaBlobMaintenanceSystem : EntitySystem
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private IGameTiming _timing = default!;
    private sealed record Completion(NCMetaBlobMaintenanceResult Blobs, int ProjectsRetired);
    private Task<Completion>? _pending;
    private TimeSpan _next;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_pending is { IsCompleted: true } completed)
        {
            try
            {
                var result = completed.GetAwaiter().GetResult();
                var debt = result.Blobs.RevisionsRemoved >= 32 || result.Blobs.BlobsRemoved >= 32 ||
                    result.ProjectsRetired >= ServerDbBase.NCMetaMaximumProjectRetirements;
                _next = _timing.RealTime + (debt ? TimeSpan.Zero : result.Blobs.BackfillComplete
                    ? TimeSpan.FromMinutes(1) : TimeSpan.FromSeconds(1));
            }
            catch (Exception exception)
            {
                Log.Error($"META storage maintenance failed closed; no unreferenced blob collection was committed: {exception}");
                _next = _timing.RealTime + TimeSpan.FromMinutes(1);
            }
            _pending = null;
        }
        if (_pending != null || _timing.RealTime < _next || !_persistence.TryCaptureTime(out var epoch, out var time)) return;
        var lease = _persistence.TryBeginNativeMutation();
        if (lease == null) return;
        _pending = MaintainAsync(lease, epoch, time);
    }

    private async Task<Completion> MaintainAsync(IDisposable lease, NCSessionEpoch epoch, NCActiveWorldTime time)
    {
        using (lease)
        {
            var retired = await _database.RetireNCMetaProjectsAsync(epoch, time);
            return new(await _database.MaintainNCMetaBlobsAsync(), retired);
        }
    }
}
