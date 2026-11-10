// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Medical.Pharmacology.Director;

public enum NCPharmaDirectorTab : byte
{
    Overview,
    Compounds,
    Formulas,
    Batches,
    Experiments,
    Cases,
    Journal,
}

/// <summary>One selectable line of a tab. The detail is shown when it is selected.</summary>
[Serializable, NetSerializable]
public readonly record struct NCPharmaDirectorRow(string Id, string Text, string Detail);

/// <summary>
/// The full pharmaceutical truth for an administrator. It is sent only to a session that opened the director,
/// which only an administrator can do.
/// </summary>
[Serializable, NetSerializable]
public sealed class NCPharmaDirectorEuiState(NCPharmaDirectorRow[][] tabs) : EuiStateBase
{
    /// <summary>Rows of each tab, indexed by <see cref="NCPharmaDirectorTab"/>.</summary>
    public readonly NCPharmaDirectorRow[][] Tabs = tabs;
}

[Serializable, NetSerializable]
public sealed class NCPharmaDirectorRefreshMessage : EuiMessageBase;

[Serializable, NetSerializable]
public sealed class NCPharmaDirectorRecallMessage(Guid batchId) : EuiMessageBase
{
    public readonly Guid BatchId = batchId;
}

/// <summary>Staged event: set the real purity and sterility of a batch. Negative means leave unchanged.</summary>
[Serializable, NetSerializable]
public sealed class NCPharmaDirectorAdjustMessage(Guid batchId, float purity, float sterility) : EuiMessageBase
{
    public readonly Guid BatchId = batchId;
    public readonly float Purity = purity;
    public readonly float Sterility = sterility;
}

/// <summary>Grants what the laboratories know of a compound: 1 chemical, 2 in-vitro, 3 in-vivo.</summary>
[Serializable, NetSerializable]
public sealed class NCPharmaDirectorKnowledgeMessage(string compound, byte level) : EuiMessageBase
{
    public readonly string Compound = compound;
    public readonly byte Level = level;
}
