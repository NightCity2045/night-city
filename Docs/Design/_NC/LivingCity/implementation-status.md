# Living City implementation status

Updated: 2026-08-16

This file tracks implemented foundations against the full dependency plan. It is not an MVP or vertical-slice scope.

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
- Mapping-authorized CityGraph diagnostics: server summary/validation/zone inspection commands plus an on-demand client
  overlay for zones, rooms, portals, costs, logical floors and validation severity.
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
- Group materialization validates active, co-located members before spawning. A child failure waits for every in-flight
  actor request to settle, then compensates only entities created by that group request. No partial formation is
  published. Group dematerialization performs a bounded all-member safety preflight and uses the atomic per-actor
  root/health/inventory hand-off; post-commit relevance still wins and is reported as an explicit partial failure.
- Relevance waits for both actor and group catalogs, reserves soft actor budgets by the full group size, and will not
  enqueue a group dematerialization while any member is in cooldown, player/camera relevance, combat, treatment or an
  explicit gameplay blocker. Formation members reject ordinary single-actor dematerialization.
- Active-round reliable outbox dispatcher with typed handler ownership, bounded polling/concurrency, per-delivery
  timeout, exponential retry and a maximum-attempt exhausted state. Delivery is at-least-once and `MessageId` is the
  mandatory idempotency key for external effects.
- Actor health and inventory subdomain aggregates. New actors receive root, default health and empty inventory in one
  serializable three-aggregate transaction, so schedulers never observe a partially initialized persistent resident.
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
- Mapping-authorized actor operations: Living City status, inspect (including safe domain summaries), deterministic
  create, freeze/unfreeze, semantic move, atomic domain capture and full medical restore, plus population
  validation/import. Corrective writes include the invoking admin identity in their persisted event payload.
- Mapping-authorized materialization operations queue one actor and inspect retained request status; `alife_status`
  exposes active, queued, in-flight and retained counts without leaking aggregate payloads.
- Mapping-authorized dematerialization and relevance operations queue/inspect reverse hand-offs, report catalog and
  cluster health, list bounded cluster summaries and derive one actor's current LOD without exposing snapshot payloads.
- Mapping-authorized zone-state operations expose bounded projector health in `alife_status`, inspect one district or
  sector without payload leakage, and restart keyset recovery during an active round.
- Mapping-authorized group inspect/create/add/remove/transfer/leader/order commands with audit source propagation.
- Mapping-authorized group materialize/dematerialize, retained-request inspection and complete runtime formation
  inspection. `alife_status` reports group catalog, queue, in-flight and physical-formation counts.
- Mapping-authorized read-only party, account, transaction and employment inspection. `alife_status` reports bounded
  cache counts; no admin command can set a balance or bypass a ledger transaction.
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

## Next dependency work

- Episodic memory, deterministic consolidation, rumors and ConversationSession remain in the social simulation phase.
  They will consume relationship/reputation authorities without reusing the player-only Agent address book as
  persistent social truth.
- Route capacity reservations plus incident-driven access/threat changes.
- Physical AI, cognition, social memory, economy, housing behavior, incidents and services in the order
  defined by the full development plan.

## Current implementation boundary

- The schedule scheduler produces semantic travel intent. The abstract executor completes CityGraph travel for
  unmaterialized actors; materialization and atomic dematerialization transfer ownership to/from a physical NPC.
  Physical movement remains delegated to the unchanged local Robust pathfinding pipeline. Dialogue and pursuit owners
  still need to acquire the implemented blocker lease when those later systems are added.
- CityGraph route selection includes traversal threat cost. Abstract elapsed duration intentionally sums the selected
  edges' base travel seconds; capacity reservations and live incident modifiers are not implemented yet.
- No production population profile or resident roster is invented until mapper-owned home/work `NCZoneId` data is
  complete. The shipped archetype, routine, importer and scheduler policies are reusable defaults, not placeholder
  residents.
- The shipped group profile defines structural policy only. No production gang, corporation squad or resident group is
  fabricated without authored faction, actor and geography data. Group split/merge planning and physical formation AI
  steering remain later systems; membership transfer and physical/strategic hand-off are implemented.
- The outbox transport and reconciliation path is production-ready infrastructure, but no production external adapter
  is registered yet. Unknown message types retry and then remain exhausted for inspection; they are never discarded.
- Actor inventory persistence currently stores stable references supplied by an economy adapter; the economy-owned
  unique-item/commodity rows and item re-materializer are later work. Vanilla inventory is neither a second authority
  nor mutated by this package. Materialization therefore accepts only an initialized empty inventory aggregate. An
  unmaterialized actor may have no full medical payload until its first safe materialization/capture; in that case the
  entity prototype's validated default medical state is used.
- The zone-state schema and ingestion boundary for scarcity, threat and control are implemented, but those values are
  intentionally unavailable until their later authoritative domains exist. The projection never substitutes a
  fabricated neutral value for missing city facts.
- The common ledger/employment/property/fine authorities are directly wired to their player-facing callers. Historical
  relational schema remains only in old EF migrations until the pre-release database baseline is squashed; no runtime
  API can use it as a second authority. No automatic employment/payroll coordinator is active yet.
