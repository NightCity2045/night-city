// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Pharmacology.Batch;

[Serializable, NetSerializable]
public enum NCPharmaReactorUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum NCPharmaReactorNotice : byte
{
    None,
    NoSource,
    NotEnoughSource,
    InvalidRequest,
    NotExperimental,
    UnitsLimit,
    Busy,
    Started,
    Finished,
    Stopped,
    Quenched,
    Degraded,
    Released,
    Ruptured,
    CatalystUsed,
    NoCatalystLeft,
}

[Serializable, NetSerializable]
public enum NCPharmaReactorAction : byte
{
    AddCatalyst,
    Quench,
    EmergencyStop,
}

/// <summary>Everything the reactor window shows. The windows of the reaction come from its prototype.</summary>
[Serializable, NetSerializable]
public sealed class NCPharmaReactorState(
    bool running,
    string formula,
    string reaction,
    float temperature,
    float temperatureRate,
    float pressure,
    float mixing,
    float catalyst,
    float progress,
    float instability,
    int heater,
    int cooler,
    bool stirring,
    bool venting,
    bool ventilation,
    int catalystLeft,
    bool sourcePresent,
    float sourceUnits,
    float sourcePurity,
    NCReactorWarnings warnings,
    NCPharmaReactorNotice notice,
    NCPharmaFormulaSummary[] formulas) : BoundUserInterfaceState
{
    public readonly bool Running = running;
    public readonly string Formula = formula;
    public readonly string Reaction = reaction;
    public readonly float Temperature = temperature;
    public readonly float TemperatureRate = temperatureRate;
    public readonly float Pressure = pressure;
    public readonly float Mixing = mixing;
    public readonly float Catalyst = catalyst;
    public readonly float Progress = progress;
    public readonly float Instability = instability;
    public readonly int Heater = heater;
    public readonly int Cooler = cooler;
    public readonly bool Stirring = stirring;
    public readonly bool Venting = venting;
    public readonly bool Ventilation = ventilation;
    public readonly int CatalystLeft = catalystLeft;
    public readonly bool SourcePresent = sourcePresent;
    public readonly float SourceUnits = sourceUnits;
    public readonly float SourcePurity = sourcePurity;
    public readonly NCReactorWarnings Warnings = warnings;
    public readonly NCPharmaReactorNotice Notice = notice;

    /// <summary>Experimental formulas that may be made here by hand.</summary>
    public readonly NCPharmaFormulaSummary[] Formulas = formulas;
}

[Serializable, NetSerializable]
public sealed class NCPharmaReactorStartMessage(string formula, int units) : BoundUserInterfaceMessage
{
    public readonly string Formula = formula;
    public readonly int Units = units;
}

/// <summary>The operator's controls. The server clamps every value.</summary>
[Serializable, NetSerializable]
public sealed class NCPharmaReactorControlMessage(
    int heater,
    int cooler,
    bool stirring,
    bool venting,
    bool ventilation) : BoundUserInterfaceMessage
{
    public readonly int Heater = heater;
    public readonly int Cooler = cooler;
    public readonly bool Stirring = stirring;
    public readonly bool Venting = venting;
    public readonly bool Ventilation = ventilation;
}

[Serializable, NetSerializable]
public sealed class NCPharmaReactorActionMessage(NCPharmaReactorAction action) : BoundUserInterfaceMessage
{
    public readonly NCPharmaReactorAction Action = action;
}
