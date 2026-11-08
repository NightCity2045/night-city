// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Numerics;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Cyberware.Optics;

[Serializable, NetSerializable]
public enum NCOpticMode : byte { Clear, LowLight, Infrared, Telescope, AntiFlash, Recording, AugmentedReality, Radar }

/// <summary>Physical optical option; compatible bay placement is owned by the cyberware assembly.</summary>
[RegisterComponent]
public sealed partial class NCOpticModuleComponent : Component
{
    [DataField] public NCOpticMode Mode;
    [DataField] public Color IrisColor = Color.Cyan;
    [DataField] public bool Cosmetic;
    [DataField] public bool Glow;
}

[RegisterComponent]
public sealed partial class NCCyberEyeComponent : Component
{
    [DataField] public bool Right;
    [DataField] public float MaximumZoom = 2;
    public NCOpticMode RequestedMode;
    public NCOpticMode ActiveMode;
    public bool AntiFlash;
    public bool AugmentedReality;
    public TimeSpan NextSample;
    public TimeSpan CommandLease;
    public float RuntimeLoad;
    public EntityUid? Body;
}

/// <summary>Removable META computer. Its durable device identity derives from the part identity.</summary>
[RegisterComponent]
public sealed partial class NCOpticControllerComponent : Component
{
    [DataField] public bool FactoryProgram;
}

[RegisterComponent]
public sealed partial class NCOpticRadarComponent : Component
{
    [DataField] public float Range = 6;
    [DataField] public float BurstSeconds = 5;
    [DataField] public float CooldownSeconds = 10;
    public TimeSpan ActiveUntil;
    [DataField(customTypeSerializer: typeof(Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.TimeOffsetSerializer))]
    public TimeSpan ReadyAt;
    public EntityUid? User;
    public readonly Dictionary<EntityUid, (Vector2 Position, TimeSpan MovedAt, int Id)> Observations = [];
    public int NextContact;
    public TimeSpan NextScan;
    public MapId ScanMap;
    public NCOpticContact[] Contacts = [];
}

/// <summary>Blocks radar segments crossing the physical entity, independently of optical transparency.</summary>
[RegisterComponent]
public sealed partial class NCOpticRadarShieldComponent : Component;

/// <summary>Only contacts the server has admitted; deliberately contains no entity IDs or identities.</summary>
[Serializable, NetSerializable]
public sealed record NCOpticContact(int Id, Vector2 Position, float Age, bool Thermal);

[Serializable, NetSerializable]
public sealed class NCOpticViewEvent : EntityEventArgs
{
    public NetEntity Viewer;
    public MapId Map;
    public NCOpticMode Mode;
    public float Quality = 1;
    public float Zoom = 1;
    public NCOpticContact[] Contacts = [];
    public string Telemetry = string.Empty;
}

[Serializable, NetSerializable]
public sealed class NCOpticSnapshotEvent : EntityEventArgs
{
    public NetEntity Viewer;
    public Guid MemoryId;
    public int Slot;
}

public static class NCOpticMath
{
    public static bool DetectMovement(Vector2 previous, Vector2 current, float elapsed, float minimumSpeed = .15f) =>
        float.IsFinite(elapsed) && elapsed > 0 && float.IsFinite(current.X) && float.IsFinite(current.Y) &&
        Vector2.DistanceSquared(previous, current) >= minimumSpeed * minimumSpeed * elapsed * elapsed;

    public static Vector2 Quantize(Vector2 value, float step) => new(
        MathF.Round(value.X / MathF.Max(.05f, step)) * MathF.Max(.05f, step),
        MathF.Round(value.Y / MathF.Max(.05f, step)) * MathF.Max(.05f, step));

    public static bool SegmentIntersectsShield(Vector2 from, Vector2 to, Vector2 center, float radius)
    {
        var delta = to - from;
        var t = delta.LengthSquared() <= float.Epsilon ? 0 : Math.Clamp(Vector2.Dot(center - from, delta) / delta.LengthSquared(), 0, 1);
        return Vector2.DistanceSquared(center, from + t * delta) <= radius * radius;
    }
}

[RegisterComponent]
public sealed partial class NCOpticMemoryComponent : Component
{
    public const int Width = 96;
    public const int Height = 64;
    public const int Bytes = Width * Height * 3;
    public const int Capacity = 4;
    [DataField] public List<byte[]> Photos = [];
    public TimeSpan NextCapture;
}

[Serializable, NetSerializable]
public sealed class NCOpticPhotoUploadEvent : EntityEventArgs
{
    public Guid Token;
    public byte[] Pixels = [];
}

[Serializable, NetSerializable]
public sealed class NCOpticAlbumRequestEvent : EntityEventArgs
{
    public NetEntity Module;
    public int Index;
    public bool Delete;
}

[Serializable, NetSerializable]
public sealed class NCOpticAlbumEvent : EntityEventArgs
{
    public NetEntity Module;
    public int Index;
    public int Count;
    public byte[] Pixels = [];
}

[Serializable, NetSerializable]
public sealed partial class NCOpticModuleDoAfterEvent : Content.Shared.DoAfter.DoAfterEvent
{
    [DataField] public NetEntity Eye;
    [DataField] public NetEntity Part;
    [DataField] public string Slot = string.Empty;
    [DataField] public uint Revision;
    [DataField] public bool Remove;
    public override Content.Shared.DoAfter.DoAfterEvent Clone() => new NCOpticModuleDoAfterEvent
    { Eye = Eye, Part = Part, Slot = Slot, Revision = Revision, Remove = Remove };
}
