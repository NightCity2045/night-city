# Living City implementation status

Updated: 2026-08-22

This file tracks implemented foundations against the full dependency plan. It is not an MVP or vertical-slice scope.

## Semi-release phase completion

| Phase | Delivered scope | Status |
| --- | --- | --- |
| 9 | Canonical commodities, stock batches, unique items, reservations, quotes, atomic settlement, payroll, physicalization and reconciliation | Complete |
| 10 | Market demand, production, businesses, shipments, cargo hand-off and shop UI | Complete |
| 11 | Property discovery, quotes, reservations, purchase/rent/resale, occupancy, access, content budgets and legal lifecycle | Complete |
| 12 | Fixer access, deals, escrow, obligations, cognition bridge and Night Market policy | Complete |
| 13 | Factions, territory, district safety, incidents, evidence/information, news, missions, cyberpsychosis and city services | Complete |
| 14 | Active-round death handling, estates, asset claims and succession | Complete |
| 15 | Agent/CitiNet city views and player-facing economic/property/service operations | Complete |
| 16 | Optional Neural Cognition Service, redacted bounded context, strict schema validation, circuit breaker and deterministic fallback | Complete |
| 17 | F7 administration/monitoring, permission checks, confirmations, audit, safe recovery operations and content audit tooling | Complete |
| 18 | Cross-domain integration, compile/content gates and focused load/regression hardening | Complete |

Validation on 2026-08-22: Shared, Server, Client and IntegrationTests build successfully; 16 focused integration/load
tests passed, one unrelated apartment-move test remains explicitly skipped; the production scale gate passed with
10,000 actors and 300 groups; 883 prototype identities passed the content audit; `git diff --check` passed.

## Implemented foundations

- Canonical persistent identities: `NCPartyRef`, actors, groups, properties, organizations, jobs, incidents, accounts,
  transactions, and active-world epochs.
- Persistent party directory and resolver for `Character`, `Actor`, `Organization`, `Business`, `CityService` and
  `Estate`. Characters resolve through the profile projection, actors through the actor aggregate, and institutional
  parties through their own versioned directory aggregate; none of those identities depend on a materialized ECS
  entity.
- Common party-owned account and immutable double-entry ledger aggregates. Every transaction has 2-16 distinct legs in
  one currency whose signed sum is zero; account projections, optimistic versions, multi-account locking, exact command
  replay and one serializable persistence batch prevent partial or duplicated money movement. Negative balances are
  fail-closed except for an explicitly typed city-service clearing account.
- Common employment relations for `Character` and `Actor` employees and organization/business/city-service employers.
  An atomically maintained per-employee index enforces one current relation; suspend, terminate and replacement flows
  preserve history. Paid relations validate employer/payee ownership, account state and currency under the same lock
  order used by the ledger.
- Party finance adapters give persistent `Character` and `Actor` identities the same deterministic primary-account
  boundary. Every balance change remains a typed double-entry ledger transaction; the adapter never accepts an
  `EntityUid` and never mutates an account projection directly.
- Account history uses bounded newest-first journal keyset pages and then validates every immutable transaction against
  the requested account. A malformed or missing transaction fails the page instead of presenting a silently incomplete
  audit log. Replayed deterministic account creation reloads the current aggregate version before later mutations.
- Atomic ledger participants let Housing, Police, stock and contract domains attach their already validated aggregate
  writes and outbox messages to the same idempotent serializable commit as money. Participant domains retain all rules;
  the ledger protects its own aggregate types and provides only the commit boundary.
- Common property aggregates reuse mapper `NCApartmentId`, `NCBuildingId` and room `NCZoneId` identities while owning
  typed owner/occupants, active grants, tenure, access revision, interior choice and settlement provenance. Rent due time
  is active-world time and cannot advance while no round is running.
- Mapper listings register directly into common property storage. Purchase, initial rent and renewal atomically commit
  property plus ledger and exact command replay cannot charge or change tenure twice.
- Player Bank, organization budgets, payroll, Housing terminals, access cards and rent lifecycle all call the common
  authorities directly. The relational ATM credential row exposes no balance, and old relational balance/property
  mutation APIs do not exist in runtime code.
- Police fines are versioned active-world aggregates. Payment transfers money to the NCPD party and marks the fine paid
  in the same ledger batch; a repeated payment resolves the original immutable transaction instead of charging again.
- ATM and organization-budget cash operations use the durable `living-city.cash-handoff` state machine. The signed
  ledger change and `PendingPhysical` state commit atomically, while exact cash entity IDs remain in a non-interactive
  terminal escrow. A deposit moves its original stack into escrow before awaiting storage and consumes it only after
  commit. A withdrawal is staged in escrow, marked complete, and only then released to the map; concurrent users cannot
  share one terminal operation. Completion and reversal are idempotent. On a later round, an interrupted deposit
  completes because its consumed cash cannot survive the old round, while an interrupted withdrawal is refunded because
  its undelivered cash cannot survive it. A persistent UUIDv7 recovery watermark prevents a full lifetime scan on every
  round start, and terminals remain closed until recovery succeeds.
- Apartment contents use a separate bounded versioned aggregate. The active-world clock raises a final-capture event and
  waits on its durability barrier before closing the epoch.
- Bank opening/adjustment and employment replacement are restart-safe across their unavoidable split durable commits.
  Recovery accepts only the exact persisted deterministic transaction/command receipt; unrelated common-domain changes
  fail closed as reconciliation conflicts instead of being overwritten.
- SQLite and PostgreSQL persistence foundation: active clock, aggregate snapshots, command receipts, event journal,
  transactional outbox, optimistic aggregate versions, and idempotent command results.
- Bounded multi-aggregate persistence commits. Up to 32 validated aggregate writes and their outbox records share one
  serializable transaction and one replayable command receipt; any version or epoch conflict commits none of them.
- Round-only active world clock with checkpointing, ordered final close, retry, and no wall-clock catch-up.
- Neutral domain mutation boundary. Domain systems decide outcomes directly; no RED Action Resolution engine, skill
  comparison, or dice formula exists in this boundary.
- Semantic CityGraph nodes derived directly from `NCZoneId`; containment is kept separate from traversable links.
- Stable identities for explicit vertical/deployment links and runtime-only identities for discovered doors/passages.
- Data-driven traversal modes, costs, capacity, threat, door profile, open-boundary profile, and mapper-door detection.
- Runtime Room/Portal Graph derived from the existing Robust pathfinding graph. It groups local navigation regions,
  discovers doors/open zone boundaries, registers cross-zone portals, and invalidates structurally per affected grid.
- Materialized-NPC route composer: CityGraph route -> Room/Portal targets -> bounded coordinates for the unchanged
  Robust tile pathfinding/steering pipeline.
- Actor-specific portal filters for access, controller, and blockage rules without polluting the shared route cache.
- Active-round physical execution owns only the hard-capped materialized actor set. It loads durable travel intent,
  gives vanilla steering one bounded local target at a time, activates authoritative door/floor-link controllers,
  blacklists a failed round-local portal per goal, replans with bounded backoff, and persists arrival, blockage or abort
  idempotently. It never requests city-wide tile pathfinding.
- YAML StateTree/HFSM profiles resolve stable physical modes from objective ECS conditions. Reactive adapters cover
  combat, perceived threat, critical state, detention, medical, police, conversation and scripted claims. Existing SS14
  NPC/HTN and owning gameplay domains remain the action executors; exclusive behavior ownership prevents them from
  competing with route steering.
- Local physical perception is materialized-only, uses faction-aware spatial queries plus actual line of sight, stores a
  bounded observation set and runs from a governor-controlled due-time queue. It neither scans all entities nor writes
  persistent beliefs directly.
- The neutral physical action boundary calls actual Robust interaction for activation and typed ECS subscribers for
  domain actions. It reports handled gameplay outcomes and contains no RED skills, dice or universal resolution formula.
- Exhausted movement uses the archetype StateTree's data-driven safe fallback: safe idle, retreat from the nearest
  perceived hostile, return to a candidate in the current public street zone, or durable plan abort. Exact failure and
  transition history is retained in a bounded deterministic trace.
- Mapping-authorized CityGraph diagnostics in the F7 Living City geography panel: bounded summary/validation/zone
  inspection, confirmed runtime activity changes, and an on-demand overlay for zones, rooms, portals, costs, logical
  floors and validation severity. Only mapper artifact helpers for ID generation and draft export remain commands.
- Persistent actor root aggregate with deterministic `NCActorId`, identity/presentation seeds, semantic location,
  lifecycle/tombstone state, schedule coordination and cognition tier. Health, finance, inventory and social facts remain
  separate authoritative aggregates instead of one monolithic actor blob.
- Lazy actor registry with optimistic versions, exactly-once command receipts, event snapshots, keyset paging, bounded
  striped mutation locks, unload/reload and terminal `Dead`/`Archived` enforcement.
- Round-local one-to-one `NCActorId <-> EntityUid` binding index. It contains only materialized actors and clears on round
  exit; persistent residents never require ECS entities.
- Data-driven cyclic active-world routines, bounded actor commitments, availability, deterministic commitment priority
  and automatic expiry from the current snapshot while immutable history remains in the event journal.
- Active-round actor schedule scheduler: asynchronous keyset loading, priority ordering by `NextUpdateAt`, per-tick and
  concurrency limits, retry with the same idempotency key after ambiguous storage failure, and no actor/entity scan in
  the hot `Update` path.
- Data-driven identity pools and population profiles with a mapping-authorized, explicitly triggered importer. Actor
  identities, command IDs and correlation IDs are deterministic; imports are concurrency-bounded, restart-safe through
  exact command receipts, and never create ECS entities.
- Active-round abstract travel executor for unmaterialized actors. It composes routes through CityGraph, derives travel
  time from traversal profiles, persists `InTransit`/arrival state, retries blocked routes without event-journal churn,
  and wakes affected actors on graph changes. Active-world time is the only travel clock.
- Materialization lifecycle notifications connect the round-local actor binding index to abstract travel. Materialized
  actors are excluded from abstract execution.
- Active-round actor materialization boundary with a bounded priority queue and bounded concurrent database loads. It
  resolves the actor's exact current `NCZoneId` through mapper-derived spawn candidates, rejects player-visible and
  actor-occupied locations, creates the archetype entity, restores identity and the complete medical payload, then
  claims the one-to-one runtime binding. Requests have retained terminal results, exact replay, retry/expiry and no
  actor/entity scan in the hot `Update` path.
- Materialization fails closed for dead/archived/frozen actors, missing mandatory aggregates and non-empty persistent
  inventories until the economy-owned item re-materializer exists. A failed health restore unbinds and deletes the new
  entity, so no partially rehydrated NPC becomes authoritative.
- Active-round player/camera relevance builds bounded clusters with merge/split distance hysteresis. Attached players
  and explicitly tagged camera/script anchors contribute mapper-derived semantic zone contexts; cluster identity is
  retained across rebuilds by membership overlap. Global and per-cluster materialization budgets are YAML policy.
- The relevance catalog recovers persistent actors through keyset pages and maintains `NCZoneId -> actors`
  incrementally from persistence events. Hot evaluation rotates through bounded zone buckets and the bounded runtime
  binding index; it never scans all persistent actors or all ECS entities. `Entity`, `Local`, `Strategic` and `City`
  LOD are derived without being persisted as a second authority.
- Reverse materialization is an atomic three-aggregate command: semantic actor root location, complete NC medical
  snapshot and stable inventory projection share one serializable transaction and one exact-replay receipt. The ECS
  entity is unbound and deleted only after `Applied`/`Duplicate`; version conflict or ambiguous storage failure retains
  the physical actor and retries safely.
- Dematerialization rechecks player/camera relevance and authoritative medical/body/inventory revision tokens after
  the asynchronous commit. An actor changed during the write is retained on the committed revision for a fresh capture,
  preventing deletion of newer physical state. Untagged inventory remains fail-closed.
- Materialization cooldown, `CombatMode`, detention/imprisonment and ref-counted gameplay blockers prevent unsafe
  disappearance. NC treatment DoAfters acquire/release the player-treatment blocker; conversation, pursuit and scripted
  systems use the same explicit lease boundary instead of being inferred by the LOD loop.
- Persistent strategic/social groups with stable `NCGroupId`, data-driven profiles, leader, current members, bounded
  membership history, directive, semantic route, resource summary, formation policy, alert state and lifecycle.
- Per-actor group-membership aggregates are committed atomically with source/destination group snapshots. Create, add,
  remove, leader succession and three-aggregate transfer preserve the one-group-per-actor invariant across crashes,
  concurrent versions and exact command retries without creating ECS entities.
- Active-round group physicalization recovers a bounded keyset-paged group catalog and coordinates every member through
  the existing actor materialization/dematerialization boundaries. Only groups explicitly marked `materializeTogether`
  are projected into relevance as one budgeted operation; individual actors retain the ordinary path.
- Complete runtime formations contain only round-local `EntityUid` bindings, stable leader-first slots, roles and
  data-driven Loose/Column/Wedge/Defensive offsets. The persistent group remains the authority for membership and
  formation policy. A group mutation or any lost actor binding invalidates the entire runtime formation immediately.
- Complete formations may share one immutable semantic trunk route. Every member still owns vanilla local steering;
  spacing is applied only to the final destination and never to exact portal targets. One member rejecting a trunk
  invalidates it for the other bounded physical members while retaining the failing member's configured replan delay.
- Group materialization validates active, co-located members before spawning. A child failure waits for every in-flight
  actor request to settle, then compensates only entities created by that group request. No partial formation is
  published. Group dematerialization performs a bounded all-member safety preflight and uses the atomic per-actor
  root/health/inventory hand-off; post-commit relevance still wins and is reported as an explicit partial failure.
- Relevance waits for both actor and group catalogs, reserves soft actor budgets by the full group size, and will not
  enqueue a group dematerialization while any member is in cooldown, player/camera relevance, combat, treatment or an
  explicit gameplay blocker. Formation members reject ordinary single-actor dematerialization.
- The active-round A-Life load governor samples an EWMA of server frame time, the measured wall-clock share of eight
  allocation-free A-Life hot paths, plus current bounded queue, due-backlog and materialized-capacity signals. YAML
  thresholds enforce the 10-15% tick target, and consecutive-sample hysteresis selects `Normal`, `Elevated` or
  `Critical`; the state is always reset when the round closes and never advances during server downtime.
- Degradation increases relevance cadence, lowers background materialization/schedule/travel/group-load dispatch and
  reduces global/per-cluster soft materialization budgets. Every asynchronous spawn first reserves a governor slot, so
  concurrent single and group requests cannot overrun the absolute hard cap. Incident, service and administrator work
  may use protected headroom only at the YAML priority required by the current state; ordinary relevance is deferred.
  Direct reverse actor hand-off retains its full dispatch budget so pressure can be relieved.
- Active-round reliable outbox dispatcher with typed handler ownership, bounded polling/concurrency, per-delivery
  timeout, exponential retry and a maximum-attempt exhausted state. Delivery is at-least-once and `MessageId` is the
  mandatory idempotency key for external effects.
- Actor health, inventory, cognition, belief-set and immutable social-profile subdomain aggregates. New actors receive
  the root and all five mandatory subdomains in one serializable six-aggregate transaction, so schedulers never
  observe a partially initialized persistent resident.
- The health adapter stores the complete validated NC Medicine snapshot as an opaque actor-owned payload and publishes
  a bounded strategic projection for condition, bleeding, mobility, consciousness, critical wounds, treatment and
  cyberware. Restore rebases monotonic medical timestamps to current runtime time, so server downtime advances nothing.
- The inventory adapter traverses only the materialized actor's held/equipped containment graph with prototype-defined
  item/depth/commodity bounds. It persists economy-supplied `NCItemId` references and commodity bundles, rejects
  duplicate stable IDs, overflow and every untagged physical item instead of deriving ownership from `EntityUid`.
- Health and inventory capture is an atomic two-aggregate command with optimistic versions and exact command replay.
  A failure in either adapter advances neither aggregate; only a successful commit updates the runtime medical-owner
  revision.
- Persistent district/sector `zone-state` aggregates are rooted directly in mapper-authored `NCZoneId` parent chains.
  Population totals, living/dead/frozen/traveling counts, status, cognition, availability and bounded archetype buckets
  are updated from actor persistence events; no extra mapper nodes and no ECS population scan are required.
- The active-round zone-state projector recovers actors through bounded keyset pages, then maintains counters
  incrementally with per-scope coalescing, bounded concurrent writes, optimistic versions and exact command retry.
  Empty authored districts/sectors also receive a valid projection, and all runtime indexes are discarded when the
  active-world epoch closes.
- City-state signals have normalized, versioned source ownership. Population activity is calculated from YAML status
  and availability weights; scarcity, threat and control remain explicitly `Unavailable` until their authoritative
  economy, incident/service and territory systems publish them. Existing authoritative signals survive population
  refreshes instead of being overwritten by defaults.
- Outbox reconciliation storage and mapping-authorized operations expose backlog/ready/exhausted counts, safe message
  metadata inspection and explicit requeue. Payloads are not printed, and processed messages cannot be reopened.
- The dedicated F7 `Living City` tab exposes a bounded overview of the active-world clock, actors, scheduler, travel,
  materialization/dematerialization, relevance, groups, population projections, canonical authorities and outbox.
  Read access and mutation access are independently checked on every server request, and all responses are bounded and
  redacted before crossing the network.
- The same F7 overview monitors the load governor state, explicit pressure reasons, smoothed frame time, measured A-Life
  work ratio, current and reserved materializations, effective soft/hard limits, maximum queue pressure, due backlogs,
  hysteresis progress, transitions and admission totals. Runtime governor control is automatic; no console or
  unaudited manual override is introduced.
- Physical execution monitoring in the same F7 tab exposes queue/load state, StateTree transitions, bounded local
  perception, neutral action results and group trunk cache counters. Actor inspection includes mode, behavior owner,
  route trace and current observations; group inspection includes the active trunk version. No operational command is
  added.
- Hybrid cognition stores actor beliefs and BDI commitment in separate stable-ID aggregates while keeping the
  Blackboard, `EntityUid` bindings, planning frontiers and caches round-local. Bounded keyed providers project actor,
  medical, inventory, employment, ledger, memory, group/faction, crime, service and local-perception authorities into
  source-versioned facts. A typed extension accepts Humanity, cyberware load and cyberpsychosis risk from their owning
  domain without recalculating them in A-Life.
- Data-driven Utility curves and considerations generate desires; minimum commitment, switching cost and explicit
  priority interrupts prevent intention twitching. Abstract actors use a lower configurable active-world cadence, and
  all cognition stops with the round instead of catching up over server downtime.
- The task-aware Plan Router dispatches routine, reactive, procedural, group, strategic and conversation tasks to
  StateTree, reactive, HTN, partial-order, resumable deterministic UCB/MCTS and conversation StateTree planners. MCTS
  compares server-authored YAML outcomes with round-local visits/reward frontiers under node and wall-time budgets. Immutable
  plans carry dependency versions and deterministic seeds; a reverse dependency cache rejects stale work and drains
  mass invalidation through bounded batches. Every route has a deterministic safe fallback and works with no neural
  provider.
- Cognition execution delegates schedule/movement and typed domain steps to their existing authorities; it never owns
  combat, medical, police or economy outcome rules and contains no RED skill/dice resolution. F7 exposes bounded
  cognition workload plus a dedicated actor inspector for facts, beliefs, desires, Utility, intention, plan and trace.
- Its typed inspector covers actors and safe domain summaries, actor relevance and retained hand-off requests, groups,
  memberships, retained physicalization requests and runtime formations, district/sector zone states, parties,
  accounts, immutable transactions, employment, directed relationships, reputation, structured episodic memory,
  immutable social profiles, rumor books, social obligations, round-local conversations, property/access summaries,
  property-content metadata, police fines and redacted outbox message metadata.
- Its actor panel provides deterministic create, freeze/unfreeze, semantic move, atomic domain capture, full medical
  restore and materialize/dematerialize operations. Its group panel provides create, add/remove/transfer member,
  leader/directive changes and coordinated physicalization. Its population panel provides profile validation,
  idempotent import and active-round zone-state projection rebuild. Its infrastructure panel provides an audited,
  exactly-confirmed outbox requeue operation and can optionally reset the attempt counter.
- Its geography panel provides zone/connectivity inspection and validation, graph and spawn-index monitoring, a
  Mapping-authorized visual overlay, and confirmed/audited changes to the active-round zone activity mode.
- Every causality-changing F7 operation requires a reason and a 15-second, single-use server confirmation bound to the
  authenticated administrator and exact request fingerprint. Accepted attempts enter the admin log; persistent domain
  mutations also receive the authenticated audit source and request correlation identity.
- The former Living City actor, group, population, common-economy, outbox, property-debug and CityGraph operational
  console command adapters have been removed. F7 is the only administrator-facing A-Life control surface; gameplay and
  schedulers continue to call canonical domain APIs directly, and no panel can set a balance or bypass a ledger
  transaction. Mapper-only ID generation and zone-editor draft export remain authoring helpers, not live operations.
- An explicit semi-release scale gate executes the production importer and persistence registries with 10,000 actors
  and 300 groups, verifies keyset recovery and cache eviction/reload, and asserts that the abstract population creates
  no ECS entities. It is deliberately excluded from ordinary fast test runs.
- Directed persistent relationships use deterministic `observer NCPartyRef -> subject NCPartyRef` identities and
  separately model trust, fear, respect, affection, hostility, perceived debt and obligation. Only a validated
  authoritative `WorldEventId` plus a YAML `ncRelationshipEffect` can change them. The relationship, bounded
  newest-first observer index and immutable application marker commit atomically, so cognition never scans the city and
  replay of an old event remains a duplicate even after later relationship changes. Agent contacts and neural output
  are not relationship authorities.
- Canonical subject reputation is distinct from directed relationships and rumors. Bounded entries explicitly separate
  personal, faction, professional and public channels; professional/public prototypes resolve through stable domain
  keys. Signed score, unsigned visibility, evidence and monotonic change order are changed only by a YAML effect plus
  authoritative `WorldEventId`. The subject snapshot and immutable provenance marker commit atomically, so an old event
  cannot apply twice and an altered initiator cannot masquerade as a valid replay. Subject-first reads require no city
  scan or unbounded audience aggregate.
- Structured episodic memory is a separate owner-first authority for characters and persistent actors. YAML event,
  emotion, retention and policy prototypes define bounded episodes with provenance, participants, salience, confidence,
  privacy, active-world expiry and permanent critical retention. Remember, deterministic consolidation and actual
  cognition recall use atomic immutable application markers. Consolidated records preserve bounded source IDs and
  semantics without generated prose; queries are privacy-bounded, and restart/replay cannot duplicate an episode.
- Every newly created persistent actor receives an immutable social-profile aggregate in the same atomic creation
  command as its root, health, inventory, cognition and belief set. Personality, values, RU/EN speech preferences and
  bounded Utility modifiers are copied from validated YAML, so rebalance does not rewrite an existing person and a
  preference cannot grant a cognition capability.
- Rumor books are bounded owner-first knowledge authorities distinct from world truth. Structured topic/subject,
  source-event provenance, immediate source, confidence, distortion, hop count, privacy and active-world retention are
  persisted; learn and propagation retries use immutable markers, and propagation never scans other actors.
- Promises, favors and debts are stable social-obligation aggregates with atomic debtor/creditor indexes and terminal,
  replay-safe outcomes. YAML consequences reach directed relationships and episodic memory through the transactional
  outbox, preserving each receiving authority instead of cross-mutating its storage.
- Conversation sessions are server-owned, bounded and round-local. They retain only semantic speech acts and external
  Agent record references, never raw message/call bodies, expire by active-world time and are cleared at round close.
  The social-context projection applies purpose and conversation disclosure ceilings before neural consumption and
  strips provenance IDs. Dialogue cannot mutate gameplay or reintroduce RED checks.
- Routine Living City administration uses a dedicated F7 tab with typed bounded requests, server-side permissions,
  exact-operation confirmations and audit. The covered A-Life console entry points no longer exist.

## Deliberately unchanged

- Vanilla RobustToolbox A*, path polygon construction, NPC steering, tile costs, and local obstacle interaction.
- `city-zones.yml` remains the semantic geography authority; the connectivity graphs are derived runtime caches.
- Normal door open/close does not rebuild Living City topology. Structural tile, fixture, door creation/deletion, map,
  deployment, and zone changes invalidate the affected cache.
- A-Life remains frozen whenever the round is not active.

## Data blockers

The current mapper data references zone IDs that are absent from `Resources/Prototypes/_NC/City/city-zones.yml`:

- housing target `67566dd3-93b9-4ec6-a823-772e5fc376fd`;
- mission target `8d7b2c44-9a31-4f67-b8e2-6c5d4a3f1808`.

Automated zone-template validation correctly rejects those references. The missing geometry must come from the mapper
export; implementation code must not invent replacement district/building/apartment/dungeon geography.

## Remaining content work

- Supply mapper-owned geometry for the two missing zone IDs listed above.
- Author production residents, organizations, businesses, factions, inventories, homes, workplaces and service rosters.
- Balance YAML economy, market, housing, incident, service, fixer and neural policies against long-running round data.
- Register a production Neural Cognition Service endpoint only when the external deployment is available; deterministic
  local cognition remains the required authority-safe fallback.

## Current implementation boundary

- The schedule scheduler produces semantic travel intent. The abstract executor completes CityGraph travel for
  unmaterialized actors; materialization and atomic dematerialization transfer ownership to/from a physical NPC.
  The completed physical executor composes hierarchical waypoints and delegates each local segment to unchanged Robust
  pathfinding. Dialogue, pursuit, service and scripted workflows use typed ownership and dematerialization blockers.
- Economy, market, property, fixer, incident, service, faction, lifecycle and neural integrations mutate only through
  their canonical aggregate systems. Cross-domain effects use atomic ledger participants or the durable outbox.
- Humanity and cyberpsychosis remain owned by their dedicated registry and medical/cyberware bridge. Cognition and the
  neural service consume bounded facts and cannot author gameplay outcomes.
- Unknown outbox message types retry and remain exhausted for F7 inspection; they are never discarded. External effects
  must use the message ID as their idempotency key.
- Historical relational schema remains only in old EF migrations until the pre-release database baseline is squashed;
  no runtime API can use it as a second authority.
- Production readiness still depends on mapper/content data and balancing. The implementation does not fabricate city
  geography, residents, faction rosters or a live external neural endpoint.
