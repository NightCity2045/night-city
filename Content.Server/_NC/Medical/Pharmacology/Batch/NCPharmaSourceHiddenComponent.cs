// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.Medical.Pharmacology.Batch;

/// <summary>
/// The hidden part of a source canister. It is not networked: a player learns of contaminants only from what
/// they do to a batch, or from a study of the compound. The stated purity says nothing about them.
/// </summary>
[RegisterComponent]
public sealed partial class NCPharmaSourceHiddenComponent : Component
{
    /// <summary>Share of the material that is a toxic contaminant. It makes every dose of the batch more toxic.</summary>
    [DataField]
    public float Contaminants;
}
