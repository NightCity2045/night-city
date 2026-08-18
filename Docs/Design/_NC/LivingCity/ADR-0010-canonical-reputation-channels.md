# ADR-0010: Canonical reputation channels

Status: Accepted  
Date: 2026-08-16

## Context

Relationship, reputation and rumor answer different questions. A directed relationship stores how one party feels
about another. Reputation stores socially attributed standing. A rumor is transferable information and may be false.
Combining those records would let a conversation, neural summary or player address book silently become social truth.

## Decision

Reputation is a canonical bounded aggregate owned by the subject `NCPartyRef`. Entries are explicitly separated into:

- `Personal`: one character or actor's scalar assessment of the subject, without replacing their richer relationship;
- `Faction`: one organization party's standing for the subject;
- `Professional`: standing inside a prototype-defined professional domain;
- `Public`: standing inside a prototype-defined public audience.

Professional and public domain prototypes have an explicit stable key. Renaming a prototype therefore does not create
a second persisted audience. Every entry stores signed score, unsigned visibility, bounded evidence count and a
monotonic last-change sequence. The sequence, rather than tick time, defines newest-first order when several events
share the same active-world timestamp.

Only an authoritative `WorldEventId`, resolved initiator and YAML `ncReputationEffect` may mutate an entry. The subject
aggregate and immutable marker derived from entry ID plus world-event ID commit in one persistent batch. The marker
also records the initiator, so replay with altered immutable provenance is rejected instead of accepted as a duplicate.

Reads are subject-first and bounded. The design deliberately does not maintain one ever-growing aggregate per public
or faction audience. Future ranking/search requirements must use a sharded disposable projection fed from persistent
events; that projection will not become reputation authority.

## Consequences

- Personal reputation is not trust/fear/affection and never overwrites a directed relationship.
- Faction reputation is not faction membership, diplomacy or territory control.
- Public reputation is not a rumor store and says nothing about which actor knows a fact.
- Neural services, Agent contacts and materialized entities can consume reputation but cannot set resulting values.
- Exact replay and active-world time survive round changes without offline progression.
- This is a direct clean-bootstrap authority; there is no relational backfill, dual-write or migration adapter.
