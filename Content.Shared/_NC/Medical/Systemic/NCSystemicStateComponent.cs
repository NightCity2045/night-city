// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Prototypes;

namespace Content.Shared._NC.Medical.Systemic;

/// <summary>
/// Server-authoritative systemic burden carried by one medical body. The bounded enum-keyed map keeps
/// exposure state extensible without introducing a component or update loop per toxin or damage source.
/// </summary>
[RegisterComponent]
public sealed partial class NCSystemicStateComponent : Component
{
    [DataField]
    public ProtoId<NCSystemicPhysiologyProfilePrototype> Profile;

    [DataField]
    public bool StateInitialized;

    [DataField]
    public Dictionary<NCSystemicChannel, float> Burdens = new();

    /// <summary>
    /// A shockable arrest remains latched until an authoritative defibrillation converts it. Merely waiting
    /// for the initiating electrical exposure to decay must not restart a stopped heart.
    /// </summary>
    [DataField]
    public bool ShockableArrestLatched;

    /// <summary>
    /// Runtime replay guard. Action correlations are intentionally excluded from persistence so a saved
    /// character cannot authorize or suppress a future server action after reload.
    /// </summary>
    [ViewVariables]
    public HashSet<NCProcessedSystemicPayloadKey> ProcessedPayloads = [];

    [ViewVariables]
    public Queue<NCProcessedSystemicPayloadKey> ProcessedPayloadOrder = [];
}

public enum NCSystemicChannel : byte
{
    Hypoxia,
    Toxin,
    Radiation,
    Cellular,
    Electrical,
    Sepsis,
}
