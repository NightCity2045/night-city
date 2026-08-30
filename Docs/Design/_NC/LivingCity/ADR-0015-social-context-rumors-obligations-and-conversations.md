# ADR-0015: Social profiles, rumors, obligations and conversations

Status: Accepted  
Date: 2026-08-21

## Context

Directed relationships, reputation and episodic memory already provide three distinct durable views of social history.
The remaining social layer needs stable actor personality, subjective information transfer, explicit promises/favors/
debts, short-lived conversation state and a privacy-safe context for later neural dialogue. None of these may turn the
player Agent address book, generated prose or a neural response into world authority.

The project also rejects the legacy RED Action Resolution engine. Social mechanics are resolved by their owning
gameplay domain without a universal skill comparison or dice boundary.

## Decision

### Stable actor profile

Each persistent actor receives one immutable `living-city.social-profile` aggregate in the same serializable commit as
the actor root and its other mandatory subdomains. The aggregate copies authored personality, values, social Utility
modifiers and RU/EN speech preferences at creation. Later YAML balancing therefore changes future actors only.

Social Utility modifiers can adjust only existing cognition capability IDs. They cannot grant capabilities or decide
the result of a gameplay action.

### Subjective information and rumors

`living-city.rumor-book` is an owner-first, bounded knowledge aggregate. A knowledge entry retains a stable information
identity, structured subject/topic, authoritative source world-event IDs, immediate source, confidence, distortion,
hop count, privacy and active-world retention. Propagation creates a new recipient-owned observation and atomically
records an immutable application marker, making retry and replay idempotent.

A rumor is what an actor believes they heard; it is never promoted to objective world truth. Confidence decreases and
distortion increases through data-driven propagation policy.

### Social obligations

Promises, favors and perceived debts use `living-city.social-obligation` aggregates addressed by stable
`NCSocialObligationId`. Debtor and creditor indexes are committed atomically with creation. Fulfilment, cancellation,
expiry and breach are terminal, idempotent transitions tied to a world event.

Obligation consequences are emitted through the transactional outbox and consumed idempotently by the existing
relationship and memory authorities. A breach can therefore cause authored trust/hostility changes and a betrayal
memory without letting the obligation service mutate either aggregate directly.

### Conversation session

`NCConversationSessionSystem` owns bounded, server-side, round-local sessions. It stores participants, channel,
structured location/topic/fact references, disclosure ceiling, language/locale, semantic speech acts and external Agent
record references. It does not copy message or call contents. Sessions expire by active-world time and all active and
recent session state is discarded when the round ends; there is no offline dialogue or catch-up.

Agent messages, groups, accepted contacts and calls can report delivered activity to this session boundary. Agent data
remains player-facing communication storage, not the canonical relationship or rumor database.

### Privacy and authority

`NCSocialContextRedactionSystem` is the canonical projection intended for neural social consumers. It applies both the
requested-purpose privacy ceiling and the active conversation's disclosure ceiling, returns bounded structured facts,
and strips world-event provenance and raw communication text. The persistent registries remain authoritative.

A conversation or neural response may propose a semantic act or phrase. Money, access, items, contracts, combat,
medical effects and every other world mutation must complete through the owning gameplay system first. Dialogue may
describe the committed result but cannot cause it by assertion. No RED checks, universal dice or central Action
Resolution engine are introduced.

### Administration

Social profile, rumor book, obligation and conversation inspection are added to the existing permission-checked F7
`Living City` surface. No A-Life console command is added.

## Consequences

- Social identity and consequences survive restarts while conversations remain intentionally round-local.
- Cognition receives relationships, rumors, obligations and immutable personality without scanning all actors.
- Rumor propagation remains traceable and replay-safe without confusing hearsay with fact.
- Later Neural Cognition integration has one bounded, redacted context boundary.
- Gameplay domains retain outcome authority; dialogue and social storage cannot bypass their rules.

## Rejected alternatives

- Using Agent contacts/messages as the persistent NPC social graph.
- Persisting raw conversation transcripts in A-Life aggregates.
- Treating neural output or generated prose as a world mutation.
- Restoring or advancing conversations, rumors or obligations through wall-clock downtime.
- Reintroducing RED skill checks, dice or a universal social Action Resolution engine.
