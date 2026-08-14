// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.Medical.Diagnostics.Scanner;

/// <summary>
/// Server-only runtime binding between a vanilla body-scanner computer and its linked medical scanner.
/// The occupant is always resolved from the scanner container; no client-selected patient is trusted.
/// </summary>
[RegisterComponent, UnsavedComponent, Access(typeof(NCBodyScannerConsoleSystem))]
public sealed partial class NCBodyScannerConsoleComponent : Component
{
    [ViewVariables]
    public EntityUid? ConnectedScanner;
}
