# ADR-0011: Structured episodic memory and deterministic consolidation

Status: Accepted  
Date: 2026-08-20

## Context

Objective facts, rumors, directed relationships, reputation and an actor's memory answer different questions. An actor
may remember an event inaccurately, learn it from another party, forget it, or retain it permanently. Dialogue text,
blackboard state and neural prose cannot safely become persistent truth.

Memory must also remain bounded for thousands of actors and must stop progressing while no round is active.

## Decision

Each character or persistent actor owns one bounded memory-book aggregate identified from its `NCPartyRef`. A canonical
episode stores only structured server-owned data:

- immutable source world-event IDs and acquisition source;
- participants and a prototype-defined semantic event type;
- salience, confidence, privacy and emotional tags;
- active-world creation, expiry and recall times;
- a retention class plus explicit critical/consolidatable policy.

Arbitrary player text and generated narrative are not accepted. Event, emotion, retention and capacity policy are YAML
prototypes. Critical events use permanent, non-consolidatable retention and cannot be silently evicted.

Remember, consolidate and recall operations each commit the memory book and an immutable application marker in one
persistent batch. Marker fingerprints bind the complete immutable request, including initiator, so an old event remains
an exact duplicate after later writes or consolidation and cannot be rebound to another meaning.

Consolidation is deterministic. Compatible episodes become a structured summary with bounded complete provenance,
participants, source kinds, semantic types, emotional tags and aggregate salience/confidence/privacy. It creates no
prose. A future neural summary is a disposable rendering of this record and never replaces it. Capacity and expiry use
active-world time; a non-expired tail that cannot form a valid bounded summary remains an episode.

Queries are owner-first, bounded and require an explicit maximum privacy level. Querying is read-only. Recall counters
change only after cognition reports the exact context it actually consumed.

## Consequences

- Objective facts and rumors remain separate future authorities.
- Materialized NPC entities and blackboards are consumers, never memory owners.
- Server downtime and time between rounds neither expires nor consolidates memory.
- A corrupt or provenance-inconsistent snapshot fails closed instead of appearing as an empty memory.
- No migration, relational backfill, dual-read or neural fallback is required for clean bootstrap.
