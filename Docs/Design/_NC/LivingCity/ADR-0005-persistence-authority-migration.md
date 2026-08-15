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
