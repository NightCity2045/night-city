// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Cyberware;

[Serializable, NetSerializable]
public sealed partial class NCCyberwarePreparationDoAfterEvent : DoAfterEvent
{
    [DataField] public NetEntity Module;
    [DataField] public uint ServiceRevision;

    public override DoAfterEvent Clone() => new NCCyberwarePreparationDoAfterEvent
    { Module = Module, ServiceRevision = ServiceRevision };
}

/// <summary>Rated supply and thermal headroom for a powered factory test. Surgery calibrates the patient separately.</summary>
public static class NCCyberwareProductionMath
{
    public static bool CanTest(float efficiency, float minimumEfficiency, float demand, float ratedPower,
        float heat, float heatRate, float cooling, float seconds, float thermalLimit)
    {
        return float.IsFinite(efficiency) && float.IsFinite(minimumEfficiency) && efficiency >= minimumEfficiency &&
               float.IsFinite(demand) && demand >= 0f && float.IsFinite(ratedPower) && demand <= ratedPower &&
               float.IsFinite(heat) && heat >= 0f && float.IsFinite(heatRate) && heatRate >= 0f &&
               float.IsFinite(cooling) && cooling >= 0f && float.IsFinite(seconds) && seconds > 0f &&
               float.IsFinite(thermalLimit) && heat + MathF.Max(0f, heatRate - cooling) * seconds < thermalLimit;
    }
}

[Serializable, NetSerializable]
public enum NCCyberwareProductionVisuals : byte
{
    Prepared,
    Empty,
    Ready,
}
