// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Shared._NC.Medical.Pharmacology.Batch;

/// <summary>Localization keys for pharmaceutical enums, so no UI ever prints a raw enum name.</summary>
public static class NCPharmaTextKeys
{
    public static string RegulatoryStatus(NCPharmaRegulatoryStatus status) => status switch
    {
        NCPharmaRegulatoryStatus.FreeSale => "nc-pharma-status-free-sale",
        NCPharmaRegulatoryStatus.Prescription => "nc-pharma-status-prescription",
        NCPharmaRegulatoryStatus.Controlled => "nc-pharma-status-controlled",
        NCPharmaRegulatoryStatus.ClinicalOnly => "nc-pharma-status-clinical-only",
        NCPharmaRegulatoryStatus.Corporate => "nc-pharma-status-corporate",
        NCPharmaRegulatoryStatus.Banned => "nc-pharma-status-banned",
        NCPharmaRegulatoryStatus.Recalled => "nc-pharma-status-recalled",
        _ => "nc-pharma-status-unknown",
    };

    public static string QcState(NCPharmaQcState state) => state switch
    {
        NCPharmaQcState.PartialQc => "nc-pharma-qc-partial",
        NCPharmaQcState.Certified => "nc-pharma-qc-certified",
        NCPharmaQcState.Rejected => "nc-pharma-qc-rejected",
        NCPharmaQcState.Recalled => "nc-pharma-qc-recalled",
        _ => "nc-pharma-qc-untested",
    };

    public static string Grade(NCPharmaGrade grade) => grade switch
    {
        NCPharmaGrade.Corporate => "nc-pharma-grade-corporate",
        NCPharmaGrade.Medical => "nc-pharma-grade-medical",
        _ => "nc-pharma-grade-street",
    };
}
