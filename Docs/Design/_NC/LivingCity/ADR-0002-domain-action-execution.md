# ADR-0002: Domain action execution

Status: Accepted  
Date: 2026-08-15

## Decision

There is no central RED Rules Engine or replacement universal Action Resolution system.

A-Life cognition selects only server-provided capabilities. Each capability is executed by the authoritative domain that owns the state:

- navigation and access handle movement and doors;
- economy handles quotes, reservations, and transfers;
- housing handles leases, ownership, and access grants;
- combat handles attacks and damage;
- medical handles treatment and physiology;
- police handles arrests, evidence, and case state.

Executors may share a correlation ID and a small lifecycle vocabulary such as succeeded, blocked, rejected, stale, or retry later. Domain-specific outcome data remains owned by the domain system.

## Consequences

- A-Life cannot mutate unrelated components or database rows directly.
- Skills, dice, and opposed RED checks are not prerequisites for the city platform.
- Existing `NCActionResolutionId` may remain a neutral correlation and deterministic identity source where already used; its name does not establish a central rules engine.
- New generic world-action payloads are avoided when a typed domain command is available.
