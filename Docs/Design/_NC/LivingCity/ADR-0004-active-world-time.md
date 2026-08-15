# ADR-0004: Active world time

Status: Accepted  
Date: 2026-08-15

## Decision

The authoritative city clock advances only while a round is active. When no round is active or the server is stopped, A-Life and every dependent timer are frozen.

No wall-clock catch-up is performed when a later round starts. Payroll, rent, debt, production, shipments, schedules, travel, health changes, relationships, faction pressure, and incidents resume from their saved active-world-time position and remaining duration.

Round end performs a flush and snapshot. Crash recovery restores the last committed state and does not simulate the outage interval.

## Consequences

- Persistent timestamps that control gameplay use active world time, not `DateTime.UtcNow`.
- Wall-clock timestamps remain appropriate for audit, operations, retention, and external request expiry.
- Existing systems that currently schedule rent or other gameplay effects with wall-clock timestamps require migration adapters.
- Interrupted incidents resume at the saved stage; a separate offline hold-for-play policy is unnecessary.
