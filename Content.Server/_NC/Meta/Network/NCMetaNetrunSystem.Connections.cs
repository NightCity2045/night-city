// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Authority;
using Content.Shared._NC.Meta.Network;

namespace Content.Server._NC.Meta.Network;

public sealed partial class NCMetaNetrunSystem
{
    private sealed record Connecting(Guid Id, EntityUid Deck, EntityUid Actor, string AccessId,
        ulong DeckEpoch, ulong DeckAuthority, ulong AccessEpoch, ulong AccessAuthority,
        NCMetaManagementProof Management, NCMetaManagementProof? AccessManagement);
    private readonly Dictionary<EntityUid, Connecting> _connecting = [];
    private readonly PriorityQueue<(EntityUid Deck, Guid Id), long> _connectionTimeouts = new();
    private bool TryQueueConnection(EntityUid deck, EntityUid actor, NCMetaDeviceEntry access,
        NCMetaManagementProof management, NCMetaManagementProof? accessManagement)
    {
        if (!_policies.TryGetValue(access.Id, out var policy) || !policy.Loading || _connecting.Count >= 64 || _connectionTimeouts.Count >= 256 || !_devices.TryGetEntry(deck, out var device)) return false;
        var request = new Connecting(Guid.NewGuid(), deck, actor, access.Id, device.DeviceEpoch, device.AuthorityEpoch,
            access.DeviceEpoch, access.AuthorityEpoch, management, accessManagement);
        _connecting[deck] = request; _connectionTimeouts.Enqueue((deck, request.Id), _now + 3000);
        return true;
    }
    private void CompleteConnections(string accessId, bool loaded)
    {
        foreach (var connection in _connecting.Values.Where(connection => connection.AccessId == accessId).ToArray())
        {
            _connecting.Remove(connection.Deck);
            var valid = loaded && _configurationAuthority.IsCurrent(connection.Management) &&
                (connection.AccessManagement == null || _configurationAuthority.IsCurrent(connection.AccessManagement)) &&
                _devices.TryResolve(accessId, out var access) && access.DeviceEpoch == connection.AccessEpoch && access.AuthorityEpoch == connection.AccessAuthority && _devices.TryGetEntry(connection.Deck, out var deck) && deck.DeviceEpoch == connection.DeckEpoch && deck.AuthorityEpoch == connection.DeckAuthority;
            if (!valid || !_devices.TryResolve(accessId, out var target) || !TryConnect(connection.Deck, connection.Actor, target.Entity)) Publish(connection.Deck, connection.Actor, "nc-meta-net-connection-lost");
        }
    }
    private void CheckConnectionTimeouts()
    {
        for (var count = 0; count < 32 && _connectionTimeouts.TryPeek(out _, out var due) && due <= _now; count++)
        {
            var timeout = _connectionTimeouts.Dequeue();
            if (_connecting.TryGetValue(timeout.Deck, out var connection) && connection.Id == timeout.Id)
            { _connecting.Remove(timeout.Deck); Publish(timeout.Deck, connection.Actor, "nc-meta-net-connection-lost"); }
        }
    }
}
