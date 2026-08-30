# ADR-0008: Durable physical-cash handoffs

Status: Accepted  
Date: 2026-08-16

## Context

Electronic balances are persistent ledger aggregates, while cash stacks are volatile ECS entities that disappear at a
round boundary. A database transaction cannot atomically insert, split, spawn or delete an ECS entity. Performing the
physical action before or after an untracked ledger write would therefore permit duplication or loss when a round or
server stops between the two actions.

## Decision

ATM and organization-budget terminals use a persistent `living-city.cash-handoff` state machine plus one
non-interactive ECS escrow container per terminal. A deposit moves the exact inserted stack from the public slot into
escrow before awaiting storage. Starting the handoff atomically commits the signed ledger transaction and a
`PendingPhysical` participant snapshot; only then is that stack consumed. A rejected deposit restores the same entity
instead of spawning replacement value.

A withdrawal commits its pending debit, spawns the bounded stacks into escrow during one main-thread callback, and
marks the handoff `Completed` while those stacks are still inaccessible. Only a completed handoff releases them onto
the map. A failed pre-completion delivery deletes only the entity IDs created by that operation and atomically reverses
the original ledger delta. The terminal, rather than a terminal/user pair, owns the operation lock, so UI closure or a
second user cannot reuse the public cash slot concurrently.

Recovery runs before terminals accept new operations. A pending deposit from an earlier round becomes `Completed`:
the source cash was already consumed and cannot exist in the new round. A pending withdrawal from an earlier round
becomes `Reversed`: undelivered spawned cash cannot exist in the new round, so the account is refunded. Transaction IDs
remain stable across optimistic retries, while a failed command receipt is never reused as a new attempt.

The recovery scanner persists the newest fully processed UUIDv7 handoff key in a separate optimistic watermark
aggregate. Each round scans only newer keys. The watermark advances only after every prior-round pending handoff in
that range is resolved; invalid data or a storage failure leaves recovery unready and terminals fail closed.

## Consequences

- One physical request can change the electronic balance at most once.
- Players cannot pick up, split or spend withdrawal stacks while their durable completion is unresolved.
- Round shutdown waits for in-flight terminal operations and recovery work.
- Server downtime advances no timer and performs no cash recovery; recovery begins only when a new active round starts.
- The historical relational balance column remains an inert credential-era schema detail and cannot bypass the handoff
  or common ledger.
- A cash entity is still volatile by design. The durable handoff records the authoritative cross-boundary intent and
  deterministic recovery outcome instead of pretending an ECS mutation can join the database transaction.
- An unreconcilable database response fails closed as `OutcomeUnknown`: a deposit is not restored because the ledger
  may already have committed, and a withdrawal is not spawned. This prefers an auditable manual correction over an
  exploitable duplicate. Likewise, the tiny completed-before-release crash window is at-most-once delivery, not a
  promise of impossible database/ECS atomicity.
