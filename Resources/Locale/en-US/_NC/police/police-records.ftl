# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

ent-NCComputerPoliceRecords = NCPD records terminal
    .desc = A secured terminal for persistent Night City police dossiers.
ent-NCPoliceRecordsComputerCircuitboard = NCPD records terminal board
    .desc = A computer printed circuit board for an NCPD records terminal.

nc-police-records-title = NCPD Records
nc-police-records-search-placeholder = Search by full or partial name...
nc-police-records-search = Search
nc-police-records-results = Residents
nc-police-records-present-group = IN THE CURRENT LAUNCH
nc-police-records-registry-group = CITY REGISTRY
nc-police-records-no-results = No matching residents
nc-police-records-select-prompt = Select a resident to open their dossier.
nc-police-records-job = Employment: 
nc-police-records-current-status = Police status: 
nc-police-records-current-reason = Basis: 
nc-police-records-updated-by = Last changed by: 
nc-police-records-change-status = Change police status
nc-police-records-reason-placeholder = Enter an IC basis for this decision...
nc-police-records-apply = Apply and log
nc-police-records-history = Audit history
nc-police-records-no-history = No police actions have been recorded.
nc-police-records-history-line = {$time} | {$actor}: {$oldStatus} -> {$newStatus}. {$reason}
nc-police-records-error = The NCPD records network failed to process the request.
nc-police-records-access-denied = Your ID is not authorized to use this NCPD terminal.

nc-police-status-none = No active status
nc-police-status-questioning = Wanted for questioning
nc-police-status-suspected = Suspected
nc-police-status-wanted = Wanted
nc-police-status-detained = Detained
nc-police-status-arrested = Arrested
nc-police-status-imprisoned = Imprisoned
nc-police-status-paroled = Paroled
nc-police-status-released = Released
nc-police-status-missing = Missing
nc-police-status-dangerous = Armed and dangerous

nc-police-tab-dossier = Dossier
nc-police-tab-cases = Cases
nc-police-tab-warrants = Warrants

nc-police-cases-list = Police cases
nc-police-case-title-placeholder = Case title...
nc-police-case-create = Open case for selected resident
nc-police-case-select-prompt = Select a case to open its journal.
nc-police-case-subjects = Linked residents
nc-police-case-link-resident = Link selected resident
nc-police-case-reports = Append-only reports
nc-police-case-add-report = Add report
nc-police-case-status-reason = IC basis for status change...
nc-police-case-change-status = Change
nc-police-case-status-line = Status: {$status}
nc-police-case-subject-line = {$name} — {$role}
nc-police-case-entry-line = {$time} | {$author}: {$text}
nc-police-case-entry-status-changed = Status changed: {$oldStatus} -> {$newStatus}. {$reason}
nc-police-case-entry-subject-added = Linked resident {$name} as {$role}.

nc-police-case-status-open = Open
nc-police-case-status-underinvestigation = Under investigation
nc-police-case-status-closed = Closed
nc-police-case-status-archived = Archived
nc-police-case-role-personofinterest = Person of interest
nc-police-case-role-suspect = Suspect
nc-police-case-role-victim = Victim
nc-police-case-role-witness = Witness
nc-police-case-role-other = Other

nc-police-warrants-list = Warrant registry
nc-police-warrant-create = Issue warrant for selected resident
nc-police-warrant-case-link = Linked to case #{$caseId}
nc-police-warrant-no-case-link = Not linked to a case
nc-police-warrant-link-selected-case = Link to selected case
nc-police-warrant-resolve = Resolve warrant
nc-police-warrant-status-line = Status: {$status}
nc-police-warrant-reason-line = Basis: {$reason}
nc-police-warrant-issued-line = Issued by {$actor} at {$time}
nc-police-warrant-not-resolved = This warrant is still active.
nc-police-warrant-resolution-line = Resolved by {$actor}: {$reason}

nc-police-warrant-type-questioning = Questioning
nc-police-warrant-type-search = Search
nc-police-warrant-type-detention = Detention
nc-police-warrant-type-arrest = Arrest
nc-police-warrant-status-active = Active
nc-police-warrant-status-executed = Executed
nc-police-warrant-status-revoked = Revoked
nc-police-warrant-status-expired = Expired
