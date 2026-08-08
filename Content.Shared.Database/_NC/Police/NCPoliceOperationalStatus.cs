// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared.Database._NC.Police;

/// <summary>
/// Persistent operational status used by NCPD. It is mapped to vanilla security status only for live-round display.
/// </summary>
public enum NCPoliceOperationalStatus : byte
{
    None,
    Questioning,
    Suspected,
    Wanted,
    Detained,
    Arrested,
    Imprisoned,
    Paroled,
    Released,
    Missing,
    Dangerous,
}

public enum NCPoliceRecordEventType : byte
{
    StatusChanged,
    Correction,
}
