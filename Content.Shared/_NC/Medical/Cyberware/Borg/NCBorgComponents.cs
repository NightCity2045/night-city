// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Anatomy;
using Content.Shared.Chemistry.Reagent;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Cyberware.Borg;

/// <summary>The physical medical carrier. Only the food inlet's public state is replicated.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCBorgCoreComponent : Component
{
    [DataField]
    public List<ProtoId<Content.Shared._NC.Medical.Humanity.NCHumanityPackagePrototype>> HumanityPackages = [];
    [DataField, AutoNetworkedField]
    public bool FoodProcessorAvailable;

    [DataField, AutoNetworkedField]
    public bool ShellConnected;

    [DataField]
    public Guid CoreId;

    /// <summary>Once a brain has occupied this carrier, missing brain is real irreversible death.</summary>
    [DataField]
    public bool Occupied;

    [DataField]
    public float PowerPerSecond = 2f;

    [DataField]
    public float PerfusatePerSecond = 0.01f;

    [DataField]
    public float OxygenPerSecond = 0.01f;

    [DataField]
    public float DialysatePerSecond = 0.01f;

    [DataField]
    public float CoolantPerSecond = 0.01f;

    [DataField]
    public Dictionary<ProtoId<ReagentPrototype>, float> ConsumptionRemainders = [];

    [DataField]
    public float Temperature = 310.15f;

    [DataField]
    public TimeSpan EmpDisabledUntil;

    [ViewVariables]
    public TimeSpan LastAdvancedAt;

    [ViewVariables]
    public float Perfusion;

    [ViewVariables]
    public float Respiration;

    [ViewVariables]
    public float Clearance;

    [ViewVariables]
    public bool Powered;
}

/// <summary>A real organ providing one existing physiological function through the pod's supplies.</summary>
[RegisterComponent]
public sealed partial class NCBorgSupportProviderComponent : Component;

/// <summary>A frame's empty carrier socket. It owns no character or brain.</summary>
[RegisterComponent]
public sealed partial class NCBorgShellSocketComponent : Component;

/// <summary>Identifies the supported first standard civilian shell's actual torso subtree.</summary>
[RegisterComponent]
public sealed partial class NCBorgStandardShellComponent : Component;

[Serializable, NetSerializable]
public enum NCBorgVisuals : byte
{
    Shell,
}

public static class NCBorgSupport
{
    public static float Capacity(NCBorgCoreComponent core, NCOrganFunctionType function)
    {
        return function switch
        {
            NCOrganFunctionType.Circulation => core.Perfusion,
            NCOrganFunctionType.Respiration => core.Respiration,
            NCOrganFunctionType.Metabolism or NCOrganFunctionType.Filtration => core.Clearance,
            _ => core.Powered ? 1f : 0f,
        };
    }

    /// <summary>Finite resources can provide at most this much of an active interval.</summary>
    public static float SuppliedSeconds(float available, float rate, float seconds)
    {
        if (!float.IsFinite(available) || !float.IsFinite(rate) || !float.IsFinite(seconds) ||
            available < 0f || rate <= 0f || seconds < 0f)
            return 0f;
        return MathF.Min(seconds, available / rate);
    }
}
