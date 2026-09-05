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
- Client prediction is for responsive interactions, never authority over money, items, health, contracts, AI decisions, or persistent outcomes.
- Neural and translation services must time out safely without blocking the server. A neural outage may finish an already accepted safe command or use a bounded physical safety reflex; it must not substitute a second deterministic social brain.

## Persistent world and round lifecycle

The city is persistent but advances only while a round is active. Stopping the round freezes the authoritative world clock and every A-Life-dependent timer:

```text
validated snapshot and journal recovery
-> restore the last active-world-time state
-> restore one physical entity for every living persistent NPC
-> event-driven physical GPT simulation
-> transaction/event flush, snapshot, and invariant checks
-> frozen state with no wall-clock catch-up
```

Offline real time never advances payments, production, deliveries, recovery, sentences, travel, relationships, incidents, or scheduled operations. A later round resumes them from the same logical point and remaining duration. Crash recovery restores committed state; it does not simulate the outage interval.

Entry and exit should respect persistent state: hospitalization, imprisonment, work, home, travel, combat, custody, medical care, important inventory, and visibility. A character should not simply appear or disappear in an impossible scene.

## Geography and physical population

All city systems should share a Z-aware location model and stable semantic geography:

```text
City -> District -> Sector -> Street/Block -> Building -> Floor -> Room
```

The `City Graph` and room/portal graph provide semantic long-range routing; local physical execution narrows to ordinary tile pathfinding. They augment physical movement and never replace an NPC body. A location may simultaneously carry district, ownership, jurisdiction, access, threat, faction, visibility, density, and event-zone context.

Mappers describe the semantics and topology of space. They should not author routes or per-NPC activity anchors. Item prototypes, building types, rooms, access, and connectivity expose reusable affordances from which a physical NPC can choose and execute a route.

Every living persistent NPC owns exactly one physical server entity for the complete active-world session, including when no player is nearby. PVS only controls client visibility. Runtime materialization, dematerialization, abstract travel, relevance presence, interactive human stand-ins, and physical-population caps are forbidden.

Purely decorative client visuals may remain non-persistent, but every interactive human belongs to the physical persistent population.

### Zone templates, apartments, and dungeons

The Bible proposes one budgeted, transactional zone-template deployment layer for persistent apartment interiors and temporary dungeon runs. Both occupy pre-declared physical zones on the main city map; a dungeon is not automatically a separate map or private game instance. A multi-floor location keeps one stable zone identity across its Z fragments, validates and locks the whole geometry, applies a versioned template in slices, resolves semantic vertical links, and either commits completely or restores a captured baseline.

The durable ideas are stable typed zone/run/deployment identities, server-side compatibility and occupancy checks, separation of ownership from interior selection, access revision rather than `EntityUid` keys, extraction rules for run-scoped loot, idempotent crash recovery, and no player-visible partial rebuild. The exact `NCZoneTemplateSystem` API, record shapes, template format, cache policy, and lifecycle names are target proposals that must be reconciled with the current `_NC` zone and map code before implementation.

## A-Life direction

Physical GPT A-Life v3 uses one isolated neural decision stream per persistent NPC:

```text
physical body and subjective sensorium
-> event-driven neural wake
-> per-ActorId GPT decision
-> validated typed command batch
-> ordinary SS14 and NC action authorities
-> verified command results
-> memory, persistence, and next wake
```

Key distinctions:

- Objective facts are separate from an actor's beliefs, memories, rumors, and narrative summaries.
- NPC knowledge needs a plausible source. NCPD, factions, and individuals do not gain global knowledge magically.
- Relationships are directional facts, obligations, memories, and social states rather than a universal public reputation number.
- A centralized scheduler wakes physical actors by event or a provider-selected follow-up time; it never groups several people into one decision.
- GPT selects ordinary intentions. HTN/StateTree may execute an accepted procedure and Utility may select a bounded safety reflex, but none of them is a second normal NPC brain.
- The command coordinator validates schema, knowledge, target freshness, authority, and staleness before normal gameplay systems may mutate the world.
- Load control throttles neural and scheduled work. It never deletes, hides, abstracts, or caps physical NPC bodies.

The numerical A-Life population, frequency, and time budgets in Notion are profiling targets, not immutable constants.

## AI and neural dialogue

GPT is the primary individual decision source for every Living City NPC. It receives bounded subjective perception, retrieved memory, personality, commitments, current physical state, opportunities, and an exact server-provided action catalog. It may choose speech, sensory requests, intentions, and typed physical commands, but it never determines world truth or action success.

AI context must be narrow, structured, versioned, and limited to information the actor may know. Player text and world-authored text are untrusted input. Responses require schema, capability, knowledge, resource, authority, safety, and staleness validation. Provider failure leaves the NPC physically present and waiting after any bounded safety reflex or already accepted safe command finishes.

Inference runs outside the Robust process through the bounded cognition service; the authoritative server retains all physical execution and persistence.

## Systems that share one causal model

- **Characters and employment:** persistent records, real hiring/firing/promotion, schedules, access profiles, and organizational authority; jobs are not selected anew in the lobby.
- **Economy and property:** authoritative, idempotent transactions; real accounts, debts, escrow, commodity batches, unique item identities, ownership, persistent storage, businesses, suppliers, and transport.
- **Fixers and contracts:** contacts, credentials, clients, executors, meeting places, escrow, inventory, risk, and reputation emerge from actual relationships and resources.
- **Crime and services:** crimes, witnesses, evidence, cases, jurisdiction, dispatch, staff, vehicles, routes, contracts, and threat thresholds are explicit. NCPD, Trauma Team, and MaxTac responses are not free spawns.
- **Combat and medicine:** their existing authoritative gameplay systems resolve physical projectiles/reach, actual body region, armor layers, local trauma, wounds, bleeding, physiology, treatment, and persistent consequences. A-Life may request permitted capabilities but does not introduce a central dice or rules engine. Do not create a second body graph or treat global root HP as the intended final truth.
- **Cyberware:** participates in anatomy, integrity, diagnostics, power/EMP/maintenance, treatment, equipment, economy, and persistence; exact humanity/psychosis mechanics still require task-specific design decisions.
- **Netrunning:** intended as real server-authoritative gameplay against located networks, credentials, nodes, and connected devices. Its action math, programs, ICE, combat integration, and UI remain design work.
- **Agent/CitiNet:** a character-facing shell for communication, contacts, banking, work, contracts, documents, map, services, news, and rumors. Publications and notifications should trace back to real world state.
- **UI:** use Robust UI/XAML and a Night City style layer, not WPF/web assumptions. Prefer code-built functional layouts over baked functional text and controls in background art.

## Important open decisions

Do not treat the following as settled merely because adjacent architecture exists:

- domain-specific action details such as combat randomness, difficulty, opposed actions, critical outcomes, retries, assistance, and audit fields; there is no central Action Resolution engine;
- the character capability/proficiency/development model, progression, training, certification, active-time interpretation, and respec;
- exact session timing, in-world time scale, logout/late-join edge cases, character-slot count, spectator policy, and handling of bodies/funerals;
- economy numbers and policies such as starting capital, wage cadence, rent, taxes, credit, bankruptcy, inheritance, licensing, and prices;
- detailed netrunning rules and content;
- full neural-provider policy, launch scope, privacy, usage telemetry, and outage UX;
- launch districts, organizations, staffing, production content, equipment catalogs, and other scope choices;
- the exact contents of roadmap blocks that the Bible itself labels as undefined, including `META`;
- any proposed data record or system/class name that has not been reconciled with the current codebase.

## Delivery approach

The accepted target is a semi-release, feature-complete implementation of the connected systems rather than an MVP or a sequence of product vertical slices. Engineering phases still follow dependency order, but no intermediate phase is presented as a reduced product:

1. Lock shared authority, identity, persistence, time, and geography contracts.
2. Build the persistent city platform and migration path before dependent domain systems.
3. Develop the A-Life, socioeconomic, city-operations, UI, and optional-neural tracks against the shared contracts.
4. Include audit, recovery, localization, tests, content validation, and performance work in every phase.
5. Merge all tracks before semi-release integration, migration rehearsal, and 24-hour soak testing.

The canonical dependency plan is maintained in `.rules/A-life/Night_City_2045_A-Life_Full_Development_Plan.md`.
