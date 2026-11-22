// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Shared._NC.Meta.Devices;

/// <summary>
/// Marks a door whose opening attempts by a person are decided by META programs instead of the vanilla access reader.
/// Order matches the <c>meta.doors.AccessOutcome</c> enum variants.
/// </summary>
public enum NCMetaDoorAccessOutcome : byte { Allowed, Denied, TimedOut, Unavailable }

public readonly record struct NCMetaDoorAccessRecord(TimeSpan Time, string Subject, NCMetaDoorAccessOutcome Outcome);

[RegisterComponent]
public sealed partial class NCMetaDoorAccessComponent : Component
{
    /// <summary>How long a program may take to answer before the attempt is denied.</summary>
    [DataField] public float ResponseSeconds = 2f;

    /// <summary>Bounded server-side journal of recent outcomes. It is never serialized or replicated.</summary>
    public readonly List<NCMetaDoorAccessRecord> Journal = [];
}
