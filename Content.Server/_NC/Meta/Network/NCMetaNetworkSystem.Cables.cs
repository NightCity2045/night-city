// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Meta.Network;

namespace Content.Server._NC.Meta.Network;

public sealed partial class NCMetaNetworkSystem
{
    private readonly HashSet<EntityUid> _registeredCables = [];
    private readonly Dictionary<string, HashSet<EntityUid>> _cablesByDevice = new(StringComparer.Ordinal);
    // Route adjacency contains only two-ended cables. This bounded reverse index tracks every cached end,
    // including a loose cable or the surviving end after its peer has been unloaded.
    private readonly Dictionary<EntityUid, HashSet<EntityUid>> _cachedCablesByPort = [];
    private void RegisterCable(Entity<NCMetaNetworkCableComponent> cable)
    {
        if (!_registeredCables.Contains(cable.Owner) && _registeredCables.Count >= 4096) return;
        _registeredCables.Add(cable.Owner);
        UnindexCachedEnds(cable);
        foreach (var id in new[] { cable.Comp.DeviceAId, cable.Comp.DeviceBId })
        {
            if (!Guid.TryParseExact(id, "D", out _)) continue;
            if (!_cablesByDevice.TryGetValue(id, out var cables)) _cablesByDevice.Add(id, cables = []);
            cables.Add(cable.Owner);
        }
        cable.Comp.PortA = _devices.TryResolve(cable.Comp.DeviceAId, out var a) ? a.Entity : null;
        cable.Comp.PortB = _devices.TryResolve(cable.Comp.DeviceBId, out var b) ? b.Entity : null;
        foreach (var port in new[] { cable.Comp.PortA, cable.Comp.PortB })
        {
            if (port is not { } endpoint) continue;
            if (!_cachedCablesByPort.TryGetValue(endpoint, out var cables)) _cachedCablesByPort.Add(endpoint, cables = []);
            cables.Add(cable.Owner);
        }
        IndexCable(cable);
    }
    private void UnregisterCable(Entity<NCMetaNetworkCableComponent> cable)
    {
        _registeredCables.Remove(cable.Owner);
        UnindexCachedEnds(cable);
        foreach (var id in new[] { cable.Comp.DeviceAId, cable.Comp.DeviceBId })
            if (_cablesByDevice.TryGetValue(id, out var cables)) { cables.Remove(cable.Owner); if (cables.Count == 0) _cablesByDevice.Remove(id); }
    }
    private void UnindexCachedEnds(Entity<NCMetaNetworkCableComponent> cable)
    {
        foreach (var port in new[] { cable.Comp.PortA, cable.Comp.PortB })
            if (port is { } endpoint && _cachedCablesByPort.TryGetValue(endpoint, out var cables))
            { cables.Remove(cable.Owner); if (cables.Count == 0) _cachedCablesByPort.Remove(endpoint); }
    }
    private void ResolveCables(EntityUid port)
    {
        if (!_devices.TryGetEntry(port, out var device) || !_cablesByDevice.TryGetValue(device.Id, out var cables)) return;
        foreach (var cable in cables.ToArray()) if (TryComp<NCMetaNetworkCableComponent>(cable, out var component)) RegisterCable((cable, component));
    }
    private void UnresolveCables(EntityUid port)
    {
        if (!_cachedCablesByPort.Remove(port, out var cables)) return;
        foreach (var cable in cables.ToArray())
            if (TryComp<NCMetaNetworkCableComponent>(cable, out var component))
            { UnindexCable((cable, component)); if (component.PortA == port) component.PortA = null; if (component.PortB == port) component.PortB = null; }
    }
}
