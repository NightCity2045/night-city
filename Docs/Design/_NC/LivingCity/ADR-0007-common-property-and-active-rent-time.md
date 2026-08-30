# ADR-0007: Common property and active-world rent time

Status: Accepted  
Date: 2026-08-16

## Decision

Mutable property state is represented by one versioned `living-city.property` aggregate keyed by the existing mapper
`NCApartmentId`. `NCBuildingId` and room `NCZoneId` references point back to mapper-owned city geography; the property
domain does not create a parallel spatial hierarchy.

The aggregate owns typed owner, occupants, active access grants, tenure status, interior selection, access revision and
the last monetary settlement identity. A city-owned available/rental property uses the stable Night City Housing
Service `NCPartyRef`; purchased property uses the buyer's typed party reference.

Rent deadlines use `NCActiveWorldTime` from their creation. No server-offline or between-round wall time advances rent,
delinquency or eviction. Mapper listings are registered directly; no relational Housing row or import is created.

Purchase, initial rent and renewal in the common domain attach the validated next property snapshot to a balanced
ledger transaction. Both commit through ADR-0006 or neither commits. Replaying the command returns the original result
without charging or changing tenure twice.

## Consequences

- Geography remains authored once in `city-zones.yml`.
- Common Housing can serve characters and persistent actors without an `EntityUid` owner.
- Relational Housing has no runtime mutation API and therefore cannot diverge from the aggregate.
- The final apartment-content write participates in the active-world close barrier, so the epoch cannot close first.
