// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Runtime;
using Content.Meta.Services;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;

namespace Content.Server._NC.Meta.Services;

public sealed partial class NCMetaServiceOrderSystem
{
    private sealed record CachedOrders(string DeskId, string Party, long Expires, MetaServiceOrder[] Orders);
    private readonly Dictionary<(Guid Runtime, ulong Session), CachedOrders> _cachedOrders = [];

    private MetaValue ReadCachedOrders(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        var resultType = process.GetImport(invocation.Request.ImportId).ReturnType!.Value;
        MetaValue Error(string code) => process.CreateErrorResult(resultType, code);
        var args = invocation.Request.Arguments;
        if (invocation.Member.Name != "cachedList" || args.Count != 2 ||
            !_runtime.CanContinue(invocation, requireWaiting: false) ||
            !_apps.TryResolveAppActor(invocation.Runtime, args[0], args[1], out var actor) ||
            !_parties.TryResolve(actor, out var party) || !process.TryGetOpaqueToken(args[1], out var session) ||
            !_persistence.TryCaptureTime(out _, out var time)) return Error("PermissionRevoked");
        if (!_cachedOrders.TryGetValue((invocation.Runtime.Generation, session), out var cache) ||
            cache.DeskId != invocation.DeviceId || cache.Party != party.ToString() || cache.Expires <= time.Ticks)
            return Error("StaleState");
        var arrayType = invocation.Runtime.Artifact.GetType(resultType).Args[0];
        return process.CreateResult(resultType, process.CreateArray(arrayType,
            cache.Orders.Select(order => EncodeOrder(process, order)).ToArray()));
    }

    private void CacheOrders(Request request, MetaServiceOrder[] orders)
    {
        if (!request.Invocation.Runtime.Process.TryGetOpaqueToken(request.Session, out var session) ||
            !_persistence.TryCaptureTime(out _, out var time)) return;
        foreach (var key in _cachedOrders.Where(pair => pair.Value.Expires <= time.Ticks).Select(pair => pair.Key).ToArray())
            _cachedOrders.Remove(key);
        var keyForSession = (request.Invocation.Runtime.Generation, session);
        if (_cachedOrders.Count >= 128 && !_cachedOrders.ContainsKey(keyForSession))
            _cachedOrders.Remove(_cachedOrders.MinBy(pair => pair.Value.Expires).Key);
        _cachedOrders[keyForSession] = new(request.DeskId, request.Party,
            checked(time.Ticks + TimeSpan.FromSeconds(10).Ticks), orders.ToArray());
    }

    private void InvalidateOrders(string deskId)
    {
        foreach (var key in _cachedOrders.Where(pair => pair.Value.DeskId == deskId).Select(pair => pair.Key).ToArray())
            _cachedOrders.Remove(key);
    }

    private void OnRuntimeStopped(NCMetaRuntimeInstance runtime)
    {
        foreach (var key in _cachedOrders.Keys.Where(key => key.Runtime == runtime.Generation).ToArray())
            _cachedOrders.Remove(key);
    }

    private static Guid CommandId(Request request, string label) => NCMetaPersistenceSystem.StableGuid(
        "service:" + request.DeskId + ":" + request.Party + ":" + request.ActionId.ToString("D") + ":" + label);

    private async Task<Guid?> ResolveOrderReference(Request request, string reference)
    {
        if (Guid.TryParseExact(reference, "D", out var exact)) return exact;
        if (reference.Length != 12 || !reference.StartsWith("ORD-", StringComparison.OrdinalIgnoreCase) ||
            !reference.AsSpan(4).ToString().All(char.IsAsciiHexDigit)) return null;
        var board = await ReadBoard(request.DeskId, request.Provider);
        Guid? match = null;
        foreach (var id in board.Orders)
        {
            if (!id.ToString("D").StartsWith(reference[4..], StringComparison.OrdinalIgnoreCase)) continue;
            if (match != null) return null;
            match = id;
        }
        return match;
    }
}
