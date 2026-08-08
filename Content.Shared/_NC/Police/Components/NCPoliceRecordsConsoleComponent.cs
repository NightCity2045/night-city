// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Police.Components;

/// <summary>
/// Data-only configuration for the persistent NCPD records terminal.
/// Per-user selections are intentionally kept in the server system, not on the shared console entity.
/// </summary>
[RegisterComponent]
public sealed partial class NCPoliceRecordsConsoleComponent : Component
{
    [DataField]
    public uint MaximumSearchLength = 64;

    [DataField]
    public uint MaximumReasonLength = 256;

    [DataField]
    public int SearchResultLimit = 100;

    [DataField]
    public int HistoryLimit = 100;

    [DataField]
    public int CaseListLimit = 100;

    [DataField]
    public int CaseEntryLimit = 100;

    [DataField]
    public int WarrantListLimit = 100;

    [DataField]
    public int FineListLimit = 200;

    [DataField]
    public TimeSpan FineDuePeriod = TimeSpan.FromDays(7);
}
