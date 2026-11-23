// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Meta.UI;

/// <summary>Filtered physical measurements for the native service tools, never a guest hardware capability.</summary>
[Serializable, NetSerializable]
public sealed record NCMetaHardwareSummary(string DeviceId, string Name, string Profile,
    string PowerKey, string HealthKey, bool Powered, bool Maintenance, int Processes, int MaximumProcesses,
    long ReservedMemoryBytes, long MemoryBytes, long WindowGas, long GasPerWindow, int ThrottlePermille,
    float LoadFactor, float AppliedWatts, float? TemperatureKelvin, int LinkReserve = 0, int LinkCapacity = 0,
    int Reliability = 1000, int WearPermille = 0, int Failures = 0);

/// <summary>Native modes reuse the ordinary installer, license, consent and recovery operations.</summary>
[Serializable, NetSerializable]
public sealed record NCMetaNativeToolsState(string View, List<NCMetaHardwareSummary> Hardware, string ChipDeviceId = "", string ChipName = "",
    List<NCMetaConnectedDevice>? Network = null, bool CanOpenStudio = false,
    List<NCMetaLocalSite>? LocalSites = null, string HostDeviceId = "");

/// <summary>A site published for the local network only. Address is what OpenLocalSite takes back.</summary>
[Serializable, NetSerializable]
public sealed record NCMetaLocalSite(string Address, string Host, string View);

/// <summary>A device this host can reach, with the capacity its link reserves and whether a program uses it.</summary>
[Serializable, NetSerializable]
public sealed record NCMetaConnectedDevice(string DeviceId, string Name, int Cost, bool InUse);

/// <summary>The chip sitting in the device's slot: open chips can be read by anyone, closed ones only by their owner.</summary>
[Serializable, NetSerializable]
public sealed record NCMetaChipSummary(string DeviceId, string Name, bool Open, bool Owned, bool CanHack = false,
    bool Cracked = false, int Attempts = 0, string LastIntruder = "");

/// <summary>The item slot a device offers for a META program chip.</summary>
public static class NCMetaChipSlot
{
    public const string Id = "nc_meta_chip";
}
