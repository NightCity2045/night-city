# ADR-0009: Directed social relationships

Status: Accepted  
Date: 2026-08-16

## Context

An actor may trust, fear or owe another party without that feeling being reciprocal. Agent contacts and messages are
player communication records, not the social state of persistent NPCs. Neural text is also non-authoritative and cannot
be allowed to write relationship values directly.

## Decision

A relationship is a deterministic directed aggregate identified by `observer NCPartyRef -> subject NCPartyRef`.
It separately stores bounded trust, fear, respect, affection, hostility, perceived debt and obligation. Positive debt
means the observer believes they owe the subject; negative debt means they believe the subject owes them.

Mutations require an authoritative `WorldEventId`, an active initiator and a validated YAML
`ncRelationshipEffect`. The server applies the configured deltas and active-world timestamp; clients and neural
providers cannot submit raw resulting scores.

Every mutation atomically advances three aggregates:

1. the directed relationship;
2. a bounded newest-first index owned by the observer;
3. an immutable application marker derived from relationship ID plus world-event ID.

The marker makes event replay exact for the lifetime of the record, including after newer events replace the
relationship's `LastWorldEventId`. The observer index lets cognition load a bounded relationship context without a
global aggregate scan. Corrupt relationship/index data fails closed instead of appearing as an empty social history.

## Consequences

- Reverse relationships are never inferred or automatically manufactured.
- A single world event can affect several observers by issuing separate directed applications with different profiles.
- Agent contacts, conversations and neural summaries may read this domain but cannot become a second authority.
- Reputation, episodic memory, rumors, promises and conversation sessions remain separate aggregates with explicit
  integration boundaries.
- Social time advances only during an active round.
