# ADR-0014: Hybrid cognition, BDI commitment and plan routing

Status: Superseded by Physical GPT A-Life v3
Date: 2026-08-21

This ADR is retained as history only. Utility/BDI/Planner may not choose ordinary NPC intentions in v3; GPT is the
primary per-ActorId decision source and server systems validate and execute its typed commands.

## Decision

Living City cognition is an active-round, server-authoritative pipeline built from versioned facts, actor beliefs,
data-driven Utility scoring, BDI commitment and a task-aware Plan Router. It is fully operational without a Neural
Cognition Service and contains no RED dice, skill-check formula or universal action-resolution rules.

The durable cognition boundary stores stable IDs only: actor cognition, belief set, current desire and current
intention are independent versioned aggregates and never contain `EntityUid`. Facts are read from the owning actor,
medical, inventory, employment, ledger, social memory, group/faction and service domains through bounded keyed reads.
Each fact carries its authoritative source and source version. Additional domains, including Humanity, cyberware load
and cyberpsychosis risk, publish their already calculated values through the typed fact collection event; cognition is
not allowed to reproduce those calculations.

A round-local Blackboard exists only for tracked Local/Entity actors. Abstract actors keep only the durable BDI state
and are evaluated at a lower data-driven cadence. Belief freshness, confidence decay, capacity, intention lifetime,
minimum commitment, switching cost and interruption thresholds are all prototype data. A small Utility change cannot
replace a committed intention; an explicit higher-priority interrupt can.

The Plan Router maps task kinds to bounded planners:

- routine to StateTree;
- immediate reaction to the reactive executor;
- known procedure to HTN;
- group operation to a validated partial-order graph;
- strategic choice to resumable deterministic MCTS;
- social conversation to a conversation StateTree.

Every planner has an explicit deterministic fallback. Plans are immutable snapshots with stable dependency versions,
deterministic seeds and replayable traces. The runtime caches plans by actor/intention/dependencies, maintains reverse
dependency consumers, rejects stale plans before execution and drains invalidation through a bounded queue. MCTS uses
a round-local resumable UCB frontier over server-authored YAML options. Deterministic rollouts accumulate visits and
reward under per-planner node/time limits plus a three-millisecond total planning limit; it cannot monopolize a tick.

## Execution boundary

Cognition selects intent and an ordered/partially ordered plan, but does not own domain outcomes. Routine movement is
delegated to the existing schedule and physical execution layers. Combat, medical, police, conversation and future
economy actions must explicitly claim a typed step and remain the only authority for their rules and mutations. An
unclaimed, invalid, stale or over-budget plan fails into the configured safe deterministic fallback.

## Failure and load policy

- All actor discovery, fact sets, queues, caches, traces, plan steps and per-tick dispatches are bounded.
- Abstract evaluation has a minimum one-active-minute interval; materialized evaluation retains responsive cadence.
- The common A-Life governor scales dispatch and evaluation under pressure and reports cognition backlog/work reasons.
- A round close discards every Blackboard, frontier, cache and `EntityUid` binding; server downtime advances nothing.
- Prototype validation fails closed for incomplete routes, cyclic procedures, invalid curves or unsafe budgets.
- Mass dependency changes schedule bounded replanning batches instead of synchronously replanning all affected actors.

## Administration

The F7 Living City tab exposes bounded cognition counters and a dedicated actor cognition inspector for authoritative
facts, beliefs, desires, Utility, intention, plan dependencies, steps and trace. It does not introduce console commands
or an administrator bypass around domain authorities.

## Consequences

- NPCs retain goals across ordinary score noise and react immediately to explicit critical interrupts.
- Materialized and abstract actors share one decision model without sharing round-local ECS identity.
- Neural refinement can be added later only as validated optional selection input; deterministic server fallback
  remains sufficient for all gameplay.
- New gameplay domains integrate by publishing versioned facts and claiming typed actions, without editing the
  cognition core or duplicating their rule calculations.
