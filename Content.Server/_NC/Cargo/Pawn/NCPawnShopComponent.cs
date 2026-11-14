// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

namespace Content.Server._NC.Cargo.Pawn;

/// <summary>Static merchant configuration. Each pair must have a unique link on its grid.</summary>
[RegisterComponent]
public sealed partial class NCPawnShopComponent : Component
{
    [DataField(required: true)] public string LinkId = string.Empty;
    [DataField] public int Percentage = 30;
    [DataField] public float MaximumPalletDistance = 4f;
}

/// <summary>A receiving area, deliberately separate from station-wide cargo selling pallets.</summary>
[RegisterComponent]
public sealed partial class NCPawnPalletComponent : Component
{
    [DataField(required: true)] public string LinkId = string.Empty;
}
