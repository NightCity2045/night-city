// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Pharmacology.Batch;

[Serializable, NetSerializable]
public enum NCPharmaStationUiKey : byte
{
    Key,
}

/// <summary>Why the station refused or what it just did. The client maps each value to localized text.</summary>
[Serializable, NetSerializable]
public enum NCPharmaStationNotice : byte
{
    None,
    NoSource,
    NotEnoughSource,
    InvalidRequest,
    ExperimentalLimit,
    Busy,
    Started,
    Finished,
    Failed,
}

/// <summary>
/// Everything the production window needs. Formulas and profiles are prototypes the client already has, so
/// only the live state of this particular machine is sent.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCPharmaStationState(
    bool sourcePresent,
    float sourceUnits,
    float sourcePurity,
    bool busy,
    float progress,
    NCPharmaStationNotice notice,
    NCPharmaFormulaSummary[] formulas) : BoundUserInterfaceState
{
    public readonly bool SourcePresent = sourcePresent;
    public readonly float SourceUnits = sourceUnits;
    public readonly float SourcePurity = sourcePurity;
    public readonly bool Busy = busy;
    public readonly float Progress = progress;
    public readonly NCPharmaStationNotice Notice = notice;

    /// <summary>Every formula that may be produced here, including versions developed in game.</summary>
    public readonly NCPharmaFormulaSummary[] Formulas = formulas;
}

/// <summary>Request to produce a known formula. The server revalidates every field.</summary>
[Serializable, NetSerializable]
public sealed class NCPharmaStationStartMessage(string formula, string profile, int units) : BoundUserInterfaceMessage
{
    public readonly string Formula = formula;
    public readonly string Profile = profile;
    public readonly int Units = units;
}
