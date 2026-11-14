// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.GameStates;

namespace Content.Shared._NC.Medical.Pharmacology.Batch;

/// <summary>
/// Raw material for a synthesis run. It is a finite physical resource with its own quality: a poor supplier
/// gives a poor batch, and a spent canister is gone.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class NCPharmaSourceComponent : Component
{
    /// <summary>Units of material left. One unit makes about one dose at standard yield.</summary>
    [DataField, AutoNetworkedField]
    public float Units = 20f;

    /// <summary>Share of the material that is the wanted compound, as the supplier states it.</summary>
    [DataField, AutoNetworkedField]
    public float Purity = 0.9f;
}
