# ADR-0005: Persistence authority migration

Status: Accepted  
Date: 2026-08-15

## Decision

Bank and Housing migrate to shared transaction, idempotency, ownership, and audit contracts without destructive replacement of existing data.

The migration order is:

1. add new tables and nullable compatibility columns;
2. backfill stable parties, opening ledger entries, and ownership projections;
3. convert old public mutation methods into adapters that call the new authority;
4. migrate all callers and validate invariants;
5. disable and remove direct legacy mutation paths after migration rehearsal;
6. retain old database columns read-only for one schema cycle;
7. remove old columns in a separate later migration after backup and restore rehearsal.

Dual-write authority is prohibited. A compatibility adapter performs only the new authoritative mutation.

## Consequences

- Existing rent flows, property interiors, snapshots, and account data remain available during migration.
- The authority switch is testable independently from physical removal of legacy columns.
- Rollback uses preserved read-only data and opening projections rather than trying to reconcile two writers.

## Implemented Bank/Jobs rehearsal

The Bank/Jobs destination projection is explicit and active-round only. It reads bounded, credential-free legacy pages
and never writes the legacy tables.

1. Run `alife_legacy_bank_jobs_rehearse verify`. Before the first projection, `MissingProjection` is expected; any
   `InvalidLegacyData` or `StorageFailure` must be fixed before apply.
2. Run `alife_legacy_bank_jobs_rehearse apply`. The acceptable completion result has zero conflicts and zero failures.
3. Run the same `apply` again. Every source row must report aligned and no row may report changed; this is the
   idempotency/restart rehearsal.
4. Run `verify` again. Every source row must align with its versioned provenance binding and target fingerprint.
5. Record the processed counts, back up both legacy and common persistence, and rehearse restore before migrating any
   player-facing caller.

`alife_legacy_bank_jobs_status` and `alife_status` expose bounded progress while a run is active. A round close cancels
the run; repeating the same mode in a later active round resumes through deterministic transaction/command receipts.

The rehearsal is a pre-switch migration tool, not a permanent post-switch synchronizer. Once common Bank/Employment is
authoritative and the legacy rows are read-only, ordinary common-domain transactions intentionally diverge from those
old rows. Running `apply` then will fail closed rather than reverse new authoritative activity.
