# ADR-0006: Atomic ledger participants

Status: Accepted  
Date: 2026-08-16

## Context

Housing claims, police fine discharge, physical-cash handoffs, stock transfer and future contract settlement change
money together with a second persistent aggregate. Calling the ledger and then changing that aggregate would leave a
crash window in either order. Keeping the old relational Bank write inside each subsystem would instead create a
second balance authority.

## Decision

The common ledger owns a neutral atomic settlement boundary. A domain may attach a bounded set of already validated,
optimistically versioned aggregate writes and outbox messages to one balanced ledger transaction. Persistence commits
all account projections, the immutable ledger transaction, participant aggregates and their messages in one
serializable batch under one idempotent command receipt.

The ledger validates only transaction invariants and the structural safety of participant writes. It does not decide
property availability, fines, prices, skills, dice, access, ownership or any other gameplay rule. Those decisions stay
inside the owning EntitySystem before the request crosses the boundary.

Participant writes may not target ledger account or transaction aggregate types. A replay returns the original money
receipt; the participant domain reloads its own aggregate and verifies the recorded transaction identity.

## Consequences

- A completed settlement cannot contain money without its property/fine/stock state, or vice versa.
- A version conflict commits none of the participant writes or ledger legs.
- Domains retain rule ownership while sharing one transaction and idempotency mechanism.
- Housing rent/purchase, police fine payment and the electronic side of a physical-cash handoff use this boundary
  directly; no relational balance or domain-state mutation path remains callable by runtime systems.
