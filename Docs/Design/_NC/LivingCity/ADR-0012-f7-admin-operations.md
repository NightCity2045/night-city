# ADR-0012: F7 Living City administration surface

Status: Accepted  
Date: 2026-08-20

## Context

Living City spans persistent actors, groups, geography, scheduling, materialization, economy, property, social state,
incidents and optional neural services. A collection of memorized console commands is not an acceptable operational
interface for a semi-release server and makes permissions, confirmations, redaction and audit difficult to review.

## Decision

All routine A-Life monitoring and administration is exposed through a dedicated `Living City` tab in the F7 admin
window. The tab uses typed bounded client/server messages and calls the same server domain APIs as simulation code. It
must never build or execute console-command strings.

The server checks admin permissions independently for every request. Read and mutation capabilities are distinct.
Potentially destructive or causality-changing actions require an explicit UI confirmation tied to the exact target and
operation. Every accepted corrective mutation records the authenticated administrator, correlation identity, reason and
result in audit/persistent event data.

Lists are paginated or keyset-bounded, sensitive payloads are redacted, and refresh is rate-limited. The interface grows
by adding domain panels over shared typed contracts; it does not create a second implementation of actor, group,
economy, property or incident logic.

The former actor, group, population, common-economy, outbox, property-debug and CityGraph operational console adapters
are removed once their typed F7 coverage is complete. Automated simulation and player-facing housing callers continue
to use the domain systems directly; removing console entry points does not introduce a UI dependency into gameplay
code. Mapper-only artifact authoring helpers may remain outside F7 because they do not operate the live A-Life state.

## Consequences

- F7 is the Definition-of-Done surface for every future admin work package.
- A domain admin operation is incomplete until its panel, permissions, confirmation, bounded response and audit path
  are implemented and tested.
- UI state is a disposable projection; canonical state always remains in the server domain systems.
- The base admin window requires a small marked `_NC` extension point, while all new tab code remains under `_NC`.
- Actor, group, population and infrastructure mutations use a 15-second single-use token bound to the authenticated
  administrator and a digest of every exact request field. A changed target, parameter or reason requires a new
  confirmation.
- Property, property-content, police-fine and outbox inspection exposes bounded metadata only. Serialized property
  contents and outbox payloads never cross the admin network boundary.
- Geography monitoring includes zone definitions, validation reports, CityGraph/RoomGraph versions, portals, rooms,
  spawn-candidate counts and an authorized visual overlay. Runtime zone activity is changed only through the confirmed,
  audited F7 action; mapper YAML remains the authority for authored geography.
