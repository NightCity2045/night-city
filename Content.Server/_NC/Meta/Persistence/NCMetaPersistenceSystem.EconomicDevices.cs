// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Server.Database;
using Content.Shared._NC.Persistence.Identity;

namespace Content.Server._NC.Meta.Persistence;

public sealed partial class NCMetaPersistenceSystem
{
    private const string EconomicDeviceType = "meta.economic-device";
    private sealed record EconomicDevice(NCItemId ItemId, string DeviceId);

    public async Task<bool> HasEconomicDeviceMappingAsync(NCItemId itemId) => itemId.IsValid &&
        await _snapshots.ReadCurrentAsync(EconomicDeviceType, itemId.ToString()) != null;

    /// <summary>Durable reverse identity lookup, including custom device IDs and unloaded physical devices.</summary>
    public async Task<string?> ReadEconomicDeviceIdAsync(NCItemId itemId)
    {
        if (!itemId.IsValid) return null;
        var snapshot = await _snapshots.ReadCurrentAsync(EconomicDeviceType, itemId.ToString());
        if (snapshot == null || snapshot.AggregateVersion != 1 || snapshot.Payload.Length > 1024) return null;
        try
        {
            var mapping = JsonSerializer.Deserialize<EconomicDevice>(snapshot.Payload, Json);
            return mapping?.ItemId == itemId && Guid.TryParseExact(mapping.DeviceId, "D", out var id) && id != Guid.Empty
                ? mapping.DeviceId : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Trusted outer CAS participant only; caller must join physical item and device owner guards.</summary>
    public async Task<(bool Valid, NCPersistentAggregateWrite? Write)> PrepareEconomicDeviceMappingAsync(NCItemId itemId, string deviceId)
    {
        if (!itemId.IsValid || !Guid.TryParseExact(deviceId, "D", out var id) || id == Guid.Empty) return (false, null);
        var snapshot = await _snapshots.ReadCurrentAsync(EconomicDeviceType, itemId.ToString());
        if (snapshot != null) return (await ReadEconomicDeviceIdAsync(itemId) == deviceId, null);
        return (true, new(Guid.NewGuid(), "meta.economic-device.claimed", EconomicDeviceType, itemId.ToString(),
            0, 1, JsonSerializer.Serialize(new EconomicDevice(itemId, deviceId), Json), "{}"));
    }
}
