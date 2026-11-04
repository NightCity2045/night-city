// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Cyberware;

[Serializable, NetSerializable]
public enum NCCyberwareNeuralDependencyState : byte
{
    None,
    Missing,
    Unavailable,
    Available,
}

/// <summary>Dependency loss never destroys the device's own calibration or neural interface.</summary>
public static class NCCyberwareNeuralDependency
{
    public static (NCCyberwareNeuralDependencyState State, float Efficiency) Evaluate(
        bool required, bool present, bool calibrated, float foundationEfficiency)
    {
        if (!required)
            return (NCCyberwareNeuralDependencyState.None, 1f);
        if (!present)
            return (NCCyberwareNeuralDependencyState.Missing, 0f);
        if (!calibrated || !float.IsFinite(foundationEfficiency) || foundationEfficiency <= 0f)
            return (NCCyberwareNeuralDependencyState.Unavailable, 0f);
        return (NCCyberwareNeuralDependencyState.Available, Math.Clamp(foundationEfficiency, 0f, 1f));
    }
}
