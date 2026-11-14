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
        NCPharmaRegulatoryStatus.Experimental => "nc-pharma-status-experimental",
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

    public static string JournalKind(NCPharmaJournalKind kind) => kind switch
    {
        NCPharmaJournalKind.Produced => "nc-pharma-journal-produced",
        NCPharmaJournalKind.QcPerformed => "nc-pharma-journal-qc",
        NCPharmaJournalKind.Administered => "nc-pharma-journal-administered",
        NCPharmaJournalKind.Approved => "nc-pharma-journal-approved",
        NCPharmaJournalKind.Experiment => "nc-pharma-journal-experiment",
        NCPharmaJournalKind.Note => "nc-pharma-journal-note",
        _ => "nc-pharma-journal-version-created",
    };

    public static string RdNotice(NCPharmaRdNotice notice) => notice switch
    {
        NCPharmaRdNotice.Created => "nc-pharma-rd-notice-created",
        NCPharmaRdNotice.Approved => "nc-pharma-rd-notice-approved",
        NCPharmaRdNotice.InvalidRequest => "nc-pharma-rd-notice-invalid",
        NCPharmaRdNotice.InvalidName => "nc-pharma-rd-notice-invalid-name",
        NCPharmaRdNotice.TooManyFormulas => "nc-pharma-rd-notice-too-many",
        NCPharmaRdNotice.NotExperimental => "nc-pharma-rd-notice-not-experimental",
        NCPharmaRdNotice.NotCertified => "nc-pharma-rd-notice-not-certified",
        NCPharmaRdNotice.Unidentified => "nc-pharma-rd-notice-unidentified",
        NCPharmaRdNotice.NoteSaved => "nc-pharma-rd-notice-note-saved",
        NCPharmaRdNotice.InvalidNote => "nc-pharma-rd-notice-invalid-note",
        _ => string.Empty,
    };

    public static string Grade(NCPharmaGrade grade) => grade switch
    {
        NCPharmaGrade.Corporate => "nc-pharma-grade-corporate",
        NCPharmaGrade.Medical => "nc-pharma-grade-medical",
        _ => "nc-pharma-grade-street",
    };
}
