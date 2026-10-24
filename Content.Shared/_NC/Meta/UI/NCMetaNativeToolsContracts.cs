// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Meta.UI;

/// <summary>Filtered physical measurements for the native service tools, never a guest hardware capability.</summary>
[Serializable, NetSerializable]
public sealed record NCMetaHardwareSummary(string DeviceId, string Name, string Profile,
    string PowerKey, string HealthKey, bool Powered, bool Maintenance, int Processes, int MaximumProcesses,
    long ReservedMemoryBytes, long MemoryBytes, long WindowGas, long GasPerWindow, int ThrottlePermille,
    float LoadFactor, float AppliedWatts, float? TemperatureKelvin);

/// <summary>Native modes reuse the ordinary installer, license, consent and recovery operations.</summary>
[Serializable, NetSerializable]
public sealed record NCMetaNativeToolsState(string View, List<NCMetaHardwareSummary> Hardware);
