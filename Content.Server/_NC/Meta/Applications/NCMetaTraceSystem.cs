// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using Content.Meta.Runtime;
using Content.Server._NC.City.Zones;
using Content.Server._NC.Meta.Commerce;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Network;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Coordinates.Systems;
using Content.Shared._NC.Meta;
using Robust.Shared.Timing;

namespace Content.Server._NC.Meta.Applications;

/// <summary>
/// Finding out where a device is, and whose. CitiNet reaches all of Night City, so any device that is on it can be
/// traced from any other that is on it; devices of a local network can be traced only from inside it. A trace is not an
/// instant lookup: it takes a few seconds, needs the route to hold for all of them, and the host that runs it can run
/// only one at a time and not often. A device that goes off the network meanwhile is lost, which is the defence.
/// </summary>
public sealed class NCMetaTraceSystem : EntitySystem, INCMetaDeviceAdapter
{
    public const int TraceSeconds = 8;
    public const int CooldownSeconds = 30;

    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaNetworkSystem _network = default!;
    [Dependency] private NCMetaMarketplaceSystem _marketplace = default!;
    [Dependency] private NCMapCoordinatesSystem _coordinates = default!;
    [Dependency] private NCZoneSystem _zones = default!;
    [Dependency] private IGameTiming _timing = default!;
    private readonly HashSet<EntityUid> _active = [];
    private readonly Dictionary<EntityUid, TimeSpan> _nextAllowed = [];
    private readonly List<NCZoneInfo> _zoneBuffer = [];

    /// <summary>Whether this host is in the middle of a trace.</summary>
    public bool IsTracing(EntityUid host) => _active.Contains(host);

    public IEnumerable<string> Members => ["meta.security.Trace.locate"];

    public bool CanBind(EntityUid device, string capability) =>
        capability == "meta.security.Trace" && HasComp<NCMetaHostComponent>(device);

    public override void Initialize()
    {
        base.Initialize();
        _runtime.RegisterAdapter(this);
    }

    public MetaValue Read(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        return process.CreateErrorResult(process.GetImport(invocation.Request.ImportId).ReturnType!.Value, "Unsupported", "Unsupported");
    }

    public MetaHostResult Commit(NCMetaDeviceInvocation invocation)
    {
        var process = invocation.Runtime.Process;
        var type = process.GetImport(invocation.Request.ImportId).ReturnType!.Value;
        MetaHostResult Fail(string code) => MetaHostResult.Completed(process.CreateErrorResult(type, code, code));
        var host = invocation.Device;
        var name = process.GetString(invocation.Request.Arguments[0]);
        // A program knows a sender by the reply address of its port, or a device by its code.
        EntityUid target;
        if (_network.TryGetPort(name, out var port)) target = port;
        else if (_devices.TryResolve(name, out var entry)) target = entry.Entity;
        else return Fail("NotFound");
        if (target == host || !CanTrace(host, target)) return Fail("NotFound");
        if (_active.Contains(host)) return Fail("Busy");
        if (_nextAllowed.TryGetValue(host, out var next) && next > _timing.CurTime) return Fail("RateLimited");
        _active.Add(host);
        _nextAllowed[host] = _timing.CurTime + TimeSpan.FromSeconds(CooldownSeconds);
        Robust.Shared.Timing.Timer.Spawn(TimeSpan.FromSeconds(TraceSeconds), () => Finish(invocation, type, target));
        return MetaHostResult.Awaiting(invocation.OperationId);
    }

    /// <summary>Both devices on CitiNet, or one network between them.</summary>
    private bool CanTrace(EntityUid from, EntityUid target) =>
        _network.CanRoute(from, target) || _marketplace.HasCitiNetRoute(from) && _marketplace.HasCitiNetRoute(target);

    private void Finish(NCMetaDeviceInvocation invocation, int type, EntityUid target)
    {
        _active.Remove(invocation.Device);
        // The route has to hold to the end: a device that was destroyed, powered down or taken off the network is no longer found.
        string? lost = null;
        NCMetaDeviceEntry? traced = null;
        if (!Exists(invocation.Device)) lost = "Disconnected";
        else if (!Exists(target)) lost = "DeviceDestroyed";
        else if (!_devices.TryGetEntry(target, out traced) || !traced.Ready || !_devices.IsPowered(target)) lost = "PowerOff";
        else if (!CanTrace(invocation.Device, target)) lost = "Disconnected";
        var place = lost == null ? Place(target) : "";
        var owner = traced == null ? "" : traced.Owner.Length == 0 ? "unowned" : traced.Owner;
        _runtime.TryComplete(invocation.Runtime, invocation.OperationId, process => lost != null || traced == null
            ? process.CreateErrorResult(type, lost ?? "NotFound", lost ?? "NotFound")
            : process.CreateResult(type, process.CreateRecord(process.GetTypeId("meta.security.TraceResult"),
                [process.CreateString(traced.Id), process.CreateString(place), process.CreateString(owner)])));
    }

    private string Place(EntityUid target)
    {
        if (!_coordinates.TryGetCoordinates(target, out var coordinates)) return "unknown";
        _zones.GetZones(coordinates, _zoneBuffer);
        var names = _zoneBuffer
            .Where(zone => zone.ContextSlot is NCZoneContextSlot.District or NCZoneContextSlot.Sector or
                NCZoneContextSlot.Street or NCZoneContextSlot.Building)
            .Select(zone => zone.Name)
            .TakeLast(3)
            .ToArray();
        return names.Length == 0 ? "unmapped" : string.Join(" / ", names);
    }
}
