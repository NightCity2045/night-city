// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Meta;

/// <summary>Hardware configuration only; runtime state and authority belong to server systems.</summary>
[RegisterComponent]
public sealed partial class NCMetaHostComponent : Component
{
    [DataField] public ProtoId<NCMetaHardwareProfilePrototype> Profile = "NCMetaEmbedded";
    [DataField] public string DeviceId = string.Empty;
    [DataField] public string OwnerKey = string.Empty;
    [DataField] public bool AllowClaim;
    [DataField] public bool Disabled;
}

[RegisterComponent]
public sealed partial class NCMetaTerminalComponent : Component
{
    [DataField] public bool AllowCompilation = true;
    [DataField] public float InteractionRange = 2;
}

[RegisterComponent]
public sealed partial class NCMetaDeviceComponent : Component
{
    [DataField] public string DeviceId = string.Empty;
    [DataField] public string OwnerKey = string.Empty;
    [DataField] public List<string> Interfaces = [];
    [DataField] public bool AllowClaim;
    [DataField] public bool AllowServiceControl;
}

[RegisterComponent]
public sealed partial class NCMetaDoorPanelComponent : Component
{
    [DataField] public string DoorId = string.Empty;
}

[RegisterComponent]
public sealed partial class NCMetaStorageComponent : Component
{
    [DataField] public int CapacityBytes = 1024 * 1024;
    [DataField] public string VolumeId = string.Empty;
}

[RegisterComponent]
public sealed partial class NCMetaPortComponent : Component
{
    [DataField] public string NetworkId = string.Empty;
    [DataField] public ProtoId<Content.Shared._NC.Meta.Network.NCMetaRadioProfilePrototype> RadioProfile = "NCMetaLocalRadio";
    [DataField] public bool Wireless;
    [DataField] public float Range = 8;
    [DataField] public int PayloadBytes = 2048;
}

[Prototype("ncMetaHardware")]
public sealed partial class NCMetaHardwareProfilePrototype : IPrototype
{
    [IdDataField] public string ID { get; private set; } = string.Empty;
    [DataField] public int Revision = 1;
    [DataField] public int MemoryBytes = 256 * 1024;
    [DataField] public int StorageBytes = 1024 * 1024;
    [DataField] public int MaxProcesses = 1;
    [DataField] public int MaxFrames = 64;
    [DataField] public int CpuWeight = 1;
    [DataField] public int GasPerWindow = 50000;
    [DataField] public int MaxBurstGas = 262144;
    [DataField] public string RuntimeAbi = "1.0";
    [DataField] public string LanguageVersion = "1.0";
    [DataField] public int MaxFileBytes = 65536;
    [DataField] public int BootMilliseconds = 250;
    [DataField] public bool AllowAutoStart = true;
    [DataField] public bool AllowUserPackages = true;
    [DataField] public bool AllowFirmwareUpdates = true;
    [DataField] public bool FirmwareRequiresMaintenance = true;
    [DataField] public bool RecoverFirmwareOnBootFailure = true;
    [DataField] public float IdleWatts = 1;
    [DataField] public float ActiveWatts = 4;
    [DataField] public float RadioWatts = 1;
    [DataField] public int RadioBytesPerSecond = 32768;
    [DataField] public float BrownoutFraction = 0.05f;
    [DataField] public string BrownoutBehavior = "Throttle";
    [DataField] public int BrownoutThrottlePermille = 250;
    [DataField] public float DegradedDamage = 25;
    [DataField] public float HardwareFaultDamage = 100;
    [DataField] public float ThrottleTemperatureKelvin = 353.15f;
    [DataField] public float ShutdownTemperatureKelvin = 383.15f;
    [DataField] public List<string> Interfaces = [];
}
