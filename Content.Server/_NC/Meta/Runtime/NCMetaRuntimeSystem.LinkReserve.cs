// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Network;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Meta;

namespace Content.Server._NC.Meta.Runtime;

public sealed partial class NCMetaRuntimeSystem
{
    /// <summary>
    /// Capacity units the host's running installations reserve for their links to other devices. A device that several
    /// programs are bound to is one physical link and counts once; the host's own storage and ports cost nothing.
    /// </summary>
    public bool TryGetLinkUsage(EntityUid host, out int used, out int capacity)
    {
        used = 0;
        capacity = 0;
        if (!TryComp<NCMetaHostComponent>(host, out var hardware) || !_prototypes.TryIndex(hardware.Profile, out var profile))
            return false;

        capacity = profile.LinkCapacity;
        var devices = new HashSet<EntityUid>();
        if (_hosts.TryGetValue(host, out var instances))
        {
            foreach (var instance in instances)
            {
                foreach (var binding in instance.Bindings.Values)
                    devices.Add(binding.Device.Entity);
            }
        }

        used = SumLinkCost(host, devices);
        return true;
    }

    /// <summary>True when starting the installation would reserve more link capacity than the host has.</summary>
    public bool IsLinkCapacityExceeded(EntityUid host, NCMetaInstallation installation)
    {
        if (!TryComp<NCMetaHostComponent>(host, out var hardware) || !_prototypes.TryIndex(hardware.Profile, out var profile))
            return false;

        _instances.TryGetValue((host, installation.Id), out var replaced);
        return !CanReserveLinks(host, profile, installation, replaced);
    }

    /// <summary>
    /// A new link is refused when its reserve does not fit the host's capacity. Links that already run are never taken
    /// away: if a cable is cut or a device moves, the host may be left over capacity and simply refuses more links.
    /// </summary>
    private bool CanReserveLinks(EntityUid host, NCMetaHardwareProfilePrototype profile, NCMetaInstallation installation,
        NCMetaRuntimeInstance? replaced)
    {
        var devices = new HashSet<EntityUid>();
        if (_hosts.TryGetValue(host, out var instances))
        {
            foreach (var instance in instances)
            {
                if (instance == replaced)
                    continue;
                foreach (var binding in instance.Bindings.Values)
                    devices.Add(binding.Device.Entity);
            }
        }

        foreach (var binding in installation.Bindings)
        {
            if (_devices.TryResolve(binding.DeviceId, out var target))
                devices.Add(target.Entity);
        }

        return SumLinkCost(host, devices) <= profile.LinkCapacity;
    }

    private int SumLinkCost(EntityUid host, IEnumerable<EntityUid> devices)
    {
        var total = 0;
        foreach (var device in devices)
        {
            if (device == host)
                continue;
            total += _network.TryGetLinkCost(host, device, out var cost) ? cost : NCMetaNetworkSystem.CableBaseCost;
        }

        return total;
    }
}
