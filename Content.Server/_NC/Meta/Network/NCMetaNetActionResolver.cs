// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Meta.Network;

namespace Content.Server._NC.Meta.Network;

public readonly record struct NCMetaNetResolution(bool Admitted, bool Success, int Trace, bool Ice, bool BlackIce);

/// <summary>Deterministic native profile resolution. ECS authority, timing and payment precede this pure calculation.</summary>
public static class NCMetaNetActionResolver
{
    public static NCMetaNetResolution Resolve(NCMetaNetToolProfilePrototype tool, NCMetaNetDefenseProfilePrototype defense,
        NCMetaNetNodeKind kind, int currentTrace)
    {
        if (!Enum.IsDefined(kind) || !tool.CompatibleKinds.Contains(kind) || tool.Rating is < 0 or > 100 || defense.Rating is < 0 or > 100 ||
            defense.TraceOnSuccess is < 0 or > 100 || defense.TraceOnFailure is < 0 or > 100 ||
            defense.IceThreshold is < 1 or > 100 || defense.BlackIceThreshold < defense.IceThreshold || defense.BlackIceThreshold > 100 || currentTrace is < 0 or > 100)
            return new(false, false, Math.Clamp(currentTrace, 0, 100), false, false);
        var success = tool.Rating >= defense.Rating;
        var trace = Math.Min(100, currentTrace + (success ? defense.TraceOnSuccess : defense.TraceOnFailure));
        return new(true, success, trace, trace >= defense.IceThreshold, trace >= defense.BlackIceThreshold);
    }
}
