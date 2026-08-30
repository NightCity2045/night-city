# ADR-0013: Physical AI execution and unchanged local pathfinding

Status: Accepted  
Date: 2026-08-21

## Decision

Materialized A-Life actors use a bounded, active-round physical execution layer above the existing RobustToolbox local
navigation. Vanilla navigation polygons, A*, steering and ordinary obstacle interaction are not replaced.

The execution hierarchy is:

1. persistent schedule/cognition supplies a semantic goal and destination `NCZoneId`;
2. CityGraph selects the bounded semantic route between authored zones;
3. Room/Portal Graph converts that route into door, boundary, floor-link and destination waypoints;
4. the physical executor gives vanilla steering exactly one local waypoint at a time;
5. portal actions pass through authoritative Robust interaction or a typed owning-domain subscriber;
6. arrival, abort or blockage is persisted through an idempotent actor mutation.

The upper physical mode is selected by a YAML StateTree/HFSM profile from objective ECS conditions. Existing SS14
HTN/NPC, combat, medical and police systems retain their action logic. The adapter only assigns exclusive behavior
ownership so route execution cannot fight those systems for control. There is no RED skill check, dice roll or universal
rules formula at this boundary.

Local perception is limited to materialized actors, uses spatial queries and line of sight, has bounded observation
sets and a governor-controlled due-time queue. Group members share an immutable semantic trunk, while each member owns
its local steering and receives formation spacing only at the final destination. A failed member invalidates the trunk
for the other members without cancelling its own configured replan backoff.

The current authored transport scope contains ordinary doors, open boundaries and explicit cross-floor links. Bus,
metro, corporate tunnel and persistent-dungeon transport links are intentionally absent until their mapper/controller
content exists.

## Failure policy

- A blocked route first rejects the failing round-local portal and replans against an alternate route.
- Bounded retries end in the archetype-selected YAML fallback: safe idle, retreat, return to a public street zone, or
  durable plan abort.
- Portal targets remain exact; group offsets never move a member away from a door or floor-link controller.
- Invalid profile data fails closed and is visible in the F7 Living City monitor.
- All queues, traces, actor sets, waypoint lists and per-tick dispatches are bounded.

## Consequences

- City-wide tile pathfinding is never requested.
- Mapper-authored `city-zones.yml` remains geography authority; runtime graphs remain derived caches.
- Physical actors react to combat, critical state, detention, medical, police, conversation and scripted claims without
  duplicating those domain rules.
- A-Life execution and perception stop and discard round-local state when the active-world round closes; server downtime
  causes no simulation catch-up.
- Future elevators or transport controllers can implement typed portal/domain actions without changing local A*.
