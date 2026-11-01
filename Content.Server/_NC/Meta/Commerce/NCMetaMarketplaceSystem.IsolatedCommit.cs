// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Server._NC.Meta.Commerce;

public sealed partial class NCMetaMarketplaceSystem
{
#if TOOLS && NC_DIAGNOSTICS
    private sealed record IsolatedCommit(Guid Request, Func<Task> Callback, DateTime Expires);
    private IsolatedCommit? _isolatedCommit;

    /// <summary>Isolated real-provider regression only; never exposed through a guest, BUI or diagnostics command.</summary>
    internal async Task SetIsolatedBeforeCommitAsync(Guid request, Func<Task> callback)
    {
        if (request == Guid.Empty || Environment.GetEnvironmentVariable("NC_DIAGNOSTICS_ISOLATED") != "1" ||
            _database is not Content.Server.Database.ServerDbManager database) throw new InvalidOperationException("Isolated commit hook is unavailable.");
        ArgumentNullException.ThrowIfNull(callback);
        // Reuse the actual-provider/environment/synchronous/in-memory guard without exposing its context.
        await database.ApplyMetaIsolatedSqliteFaultAsync(_ => { });
        await Main(() =>
        {
            if (_isolatedCommit is { } current && current.Expires > DateTime.UtcNow) throw new InvalidOperationException("An isolated commit hook already exists.");
            _isolatedCommit = new(request, callback, DateTime.UtcNow.AddSeconds(5)); return true;
        });
    }
    internal Task ClearIsolatedBeforeCommitAsync(Guid request) => Main(() =>
    { if (_isolatedCommit?.Request == request) _isolatedCommit = null; return true; });

    private async Task BeforeAuthorityCommitAsync(Guid request)
    {
        if (Environment.GetEnvironmentVariable("NC_DIAGNOSTICS_ISOLATED") != "1") return;
        var callback = await Main(() =>
        {
            if (_isolatedCommit is not { } current) return null;
            if (current.Expires <= DateTime.UtcNow) { _isolatedCommit = null; return null; }
            if (current.Request != request) return null;
            _isolatedCommit = null; return current.Callback;
        });
        if (callback != null) await callback().WaitAsync(TimeSpan.FromSeconds(5));
    }
#else
    private static Task BeforeAuthorityCommitAsync(Guid request) => Task.CompletedTask;
#endif
}
