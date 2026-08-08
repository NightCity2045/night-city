# Night City 2045 — working project context

## Purpose and source

This is a durable working summary of the [Night City 2045 Project Bible](https://app.notion.com/p/3738867306b1808fb570c3d83674bac6), reviewed on 2026-08-08 together with its 00–23 project sections and the 00–15 `Living Night City — A-Life` technical corpus.

The Notion material is a living design reference. It is not a contract to reproduce every described subsystem, data shape, name, phase, formula, limit, or technology. During implementation we may keep, change, simplify, postpone, or reject proposals after comparing them with the current repository, RobustToolbox paradigms, performance evidence, gameplay needs, and newly accepted decisions.

Use this file as orientation, then re-read the relevant Notion section when the exact distinction between approved, target, deferred, and open matters.

## Decision discipline

Keep these categories separate:

- **Current decision:** explicitly accepted by the user for the work at hand.
- **Repository fact:** what the inspected code and data currently do; this is evidence, not automatically the desired design.
- **Project direction:** a stable intent from the Bible that should inform proposals.
- **Target architecture:** a possible later end state, not necessarily MVP scope.
- **Open decision:** deliberately unresolved; do not invent a canonical answer.
- **Working assumption:** a reversible choice made to move a bounded task forward; label it as such.

When the best current solution diverges from the Bible, explain the divergence and its consequences. Update this summary only after a decision becomes durable.

## Project identity

Night City 2045 is intended as a persistent urban life-sim and RP server in the Cyberpunk RED Time of the Red setting, built on RobustToolbox/Space Station 14. A session is an active window into one continuing city, not a disposable station shift.

The desired player loop is broadly:

```text
persistent character
-> life, work, property, health, relationships, and obligations
-> intervention in a living city
-> durable consequences
-> a later session in the same world
```

Important experiential goals:

- Characters, careers, property, injuries, contacts, debts, crimes, and history belong to a persistent character rather than to a temporary round role.
- Tabletop roles are not mandatory classes. A fixer, ripper, or edgerunner becomes one through actual activity, resources, relationships, and history.
- The city should exhibit causal continuity. Information, services, goods, reinforcements, and consequences need plausible sources.
- Scarcity is systemic: production, inventory, logistics, ownership, theft, and loss matter. Shops and fixers do not create infinite supply.
- Violence is dangerous and locally traumatic, while usually leaving a window for stabilization, evacuation, and treatment instead of functioning as an arcade HP race.
- Confirmed death of a persistent character is final, with later succession and world consequences. This depends on trustworthy persistence and recovery.
- The world is specifically Night City in 2045, not the 2077 state of the setting. Content must respect the post-war scarcity, reconstruction, and contemporary status of organizations.
- RU/EN players share one community. Authored UI/system text uses Fluent localization; machine translation is a separate, non-blocking service for suitable player communication.

## Robust-native technical invariants

- Preserve strict Shared/Server/Client boundaries and server authority over world truth.
- Keep components data-only and logic in EntitySystems. Prefer events and existing engine extension points.
- Put original project code and data under the appropriate `_NC` paths. Keep upstream edits surgical and use bridges, hooks, adapters, or extension points where possible.
- Treat prototypes as configuration, not mutable world storage. Balance and policies should be data-driven rather than hardcoded.
- Persistent identity uses stable domain IDs such as `CharacterId`, `ActorId`, `PropertyId`, `BuildingId`, `RoomId`, `FactionId`, `ContractId`, and `ItemInstanceId`.
- `EntityUid`, runtime grids, transient physics state, UI sessions, blackboards, planner queues, and recalculable caches are not persistent truth.
- A runtime index may bind a stable actor ID to one current `EntityUid`; enforce the invariant that one persistent actor cannot have two live entities.
- Significant state changes must be auditable, idempotent where relevant, and recoverable. The Bible favors append-only events, versioned aggregates, snapshots, asynchronous writes, and an explicit end-of-session flush.
- Never perform synchronous persistence or external-service I/O in the game tick.
- Avoid one `Update()`/timer/planner per persistent actor, full-entity scans, and unbounded work. Use event wakeups, centralized schedulers, spatial indexes, queues, budgets, and batching.
- Client prediction is for responsive interactions, never authority over money, items, health, contracts, AI, materialization, or persistent outcomes.
- Optional AI or translation services must time out safely and degrade to deterministic behavior without blocking the server.

## Persistent world and session lifecycle

The intended lifecycle is session-driven:

```text
validated snapshot and journal recovery
-> controlled downtime
-> session preparation and entry
-> active physical and abstract simulation
-> soft close of critical scenes
-> transaction/event flush, snapshot, and invariant checks
-> controlled downtime
```

Downtime may advance routine payments, production, deliveries, recovery, sentences, travel, and scheduled operations. It should not silently resolve major interactive conflicts that are meant to become player-facing content.

Entry and exit should respect persistent state: hospitalization, imprisonment, work, home, travel, combat, custody, medical care, important inventory, and visibility. A character should not simply appear or disappear in an impossible scene.

## Geography and materialization

All city systems should share a Z-aware location model and stable semantic geography:

```text
City -> District -> Sector -> Street/Block -> Building -> Floor -> Room
```

The long-range simulation uses a `City Graph`; local physical execution narrows through room/portal routing to tile pathfinding. A location may simultaneously carry district, ownership, jurisdiction, access, threat, faction, visibility, density, and event-zone context.

Mappers describe the semantics and topology of space. They should not place gang/police/civilian/fixer spawners as the source of city life. Materialization selects valid candidates from geometry, access, capacity, route, current logical position, and visibility.

Persistent actors exist without requiring live entities. Materialize them near relevant player clusters, meetings, incidents, missions, deliveries, or service responses; dematerialize only when the physical scene is safely resumable. Never spawn or remove an important actor visibly or while combat, custody, treatment, pursuit, conversation, or a critical physical object still requires the entity.

Decorative ambient, interactive ambient, and persistent actors are separate population layers. Ambient population does not automatically acquire persistent identity.

### Zone templates, apartments, and dungeons

The Bible proposes one budgeted, transactional zone-template deployment layer for persistent apartment interiors and temporary dungeon runs. Both occupy pre-declared physical zones on the main city map; a dungeon is not automatically a separate map or private game instance. A multi-floor location keeps one stable zone identity across its Z fragments, validates and locks the whole geometry, applies a versioned template in slices, resolves semantic vertical links, and either commits completely or restores a captured baseline.

The durable ideas are stable typed zone/run/deployment identities, server-side compatibility and occupancy checks, separation of ownership from interior selection, access revision rather than `EntityUid` keys, extraction rules for run-scoped loot, idempotent crash recovery, and no player-visible partial rebuild. The exact `NCZoneTemplateSystem` API, record shapes, template format, cache policy, and lifecycle names are target proposals that must be reconciled with the current `_NC` zone and map code before implementation.

## A-Life direction

The A-Life reference describes a layered, budgeted architecture rather than one universal AI brain:

```text
world facts and events
-> deterministic rules and permitted capabilities
-> beliefs, desires, and utility
-> stable BDI intention
-> cheapest suitable planner/executor
-> server action resolution
-> ECS execution
-> event journal, memory, and new world state
```

Key distinctions:

- Objective facts are separate from an actor's beliefs, memories, rumors, and narrative summaries.
- NPC knowledge needs a plausible source. NCPD, factions, and individuals do not gain global knowledge magically.
- Relationships are directional facts, obligations, memories, and social states rather than a universal public reputation number.
- A centralized scheduler wakes distant actors/groups by event or `NextUpdateAt`; remote actors do not keep blackboards, behavior trees, tile routes, or active physical plans.
- StateTree/HFSM handles routine modes; reactive/behavior-tree layers handle local physical reactions; HTN is for known procedures; partial-order planning is for bounded group operations; strategic search is rare and optional.
- A plan router should choose the cheapest adequate mechanism. Every advanced planner needs validation, version/staleness checks, a work budget, and a deterministic fallback.
- When overloaded, reduce ambient density, distant detail, optional planning, narrative work, and neural work before harming player-critical scenes or tick stability.

The numerical A-Life population, frequency, and time budgets in Notion are profiling targets, not immutable constants.

## AI and neural dialogue

Neural cognition is optional. It may render speech or choose among server-provided social capabilities for selected important NPCs, but it must not determine world truth, action success, movement, combat, damage, treatment, money, inventory, guilt, service dispatch, or materialization.

AI context must be narrow, structured, versioned, and limited to information the actor may know. Player text and world-authored text are untrusted input. Responses require schema, capability, knowledge, resource, authority, safety, and staleness validation. Failure falls back to cached valid output, utility/BDI, authored personality templates, or a safe default.

The first viable A-Life implementation does not depend on an LLM. The current design assumes the game server has no GPU and should not host heavy inference in the Robust process.

## Systems that share one causal model

- **Characters and employment:** persistent records, real hiring/firing/promotion, schedules, access profiles, and organizational authority; jobs are not selected anew in the lobby.
- **Economy and property:** authoritative, idempotent transactions; real accounts, debts, escrow, commodity batches, unique item identities, ownership, persistent storage, businesses, suppliers, and transport.
- **Fixers and contracts:** contacts, credentials, clients, executors, meeting places, escrow, inventory, risk, and reputation emerge from actual relationships and resources.
- **Crime and services:** crimes, witnesses, evidence, cases, jurisdiction, dispatch, staff, vehicles, routes, contracts, and threat thresholds are explicit. NCPD, Trauma Team, and MaxTac responses are not free spawns.
- **Combat and medicine:** one authoritative action-resolution result feeds physical projectiles/reach, actual body region, armor layers, local trauma, wounds, bleeding, physiology, treatment, and persistent consequences. Do not create a second body graph or treat global root HP as the intended final truth.
- **Cyberware:** participates in anatomy, integrity, diagnostics, power/EMP/maintenance, treatment, equipment, economy, and persistence; exact humanity/psychosis mechanics still require task-specific design decisions.
- **Netrunning:** intended as real server-authoritative gameplay against located networks, credentials, nodes, and connected devices. Its action math, programs, ICE, combat integration, and UI remain design work.
- **Agent/CitiNet:** a character-facing shell for communication, contacts, banking, work, contracts, documents, map, services, news, and rumors. Publications and notifications should trace back to real world state.
- **UI:** use Robust UI/XAML and a Night City style layer, not WPF/web assumptions. Prefer code-built functional layouts over baked functional text and controls in background art.

## Important open decisions

Do not treat the following as settled merely because adjacent architecture exists:

- the general action-resolution algorithm, randomness, difficulty, opposed actions, critical outcomes, retries, assistance, and audit fields;
- the character capability/proficiency/development model, progression, training, certification, active-time interpretation, and respec;
- exact session timing, in-world time scale, logout/late-join edge cases, character-slot count, spectator policy, and handling of bodies/funerals;
- economy numbers and policies such as starting capital, wage cadence, rent, taxes, credit, bankruptcy, inheritance, licensing, and prices;
- detailed netrunning rules and content;
- full neural-provider policy, launch scope, privacy, budgets, and outage UX;
- launch districts, organizations, staffing, production content, equipment catalogs, and other scope choices;
- the exact contents of roadmap blocks that the Bible itself labels as undefined, including `META`;
- any proposed data record or system/class name that has not been reconciled with the current codebase.

## Delivery approach

Prefer a narrow vertical slice and gates over simultaneous construction of the end state:

1. Inspect existing Robust/SS14 and `_NC` foundations.
2. Define the smallest domain model and authority boundary needed by the current slice.
3. Include persistence, audit, recovery, localization, UI, tests, and performance implications in the design rather than retrofitting them silently.
4. Build one end-to-end behavior, profile and validate it, then expand.
5. Keep future models behind neutral contracts so unresolved character progression or action resolution does not force premature coupling.

The broad reference sequence starts with repository/persistence/geography and a map vertical slice, then resolves action mechanics before systems that depend heavily on them, and adds full A-Life only after the city has real actors, resources, routes, contracts, and persistent consequences. This ordering is guidance and may be revised for the actual repository state and current development priority.
