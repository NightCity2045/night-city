# ADR-0005: Direct persistence authority before release

Status: Accepted  
Date: 2026-08-16

## Decision

Bank, employment and Housing use the common Living City aggregates as their first and only shipped authority. The
project is pre-release, so there is no player data to import and no reason to build a compatibility cutover.

- Account balances exist only in the double-entry `living-city.account` / `living-city.ledger-transaction` model.
- Property tenure, occupants, access and active-world rent deadlines exist only in `living-city.property`.
- Apartment contents use the separate versioned `living-city.property-content` aggregate.
- Fine payment writes `living-city.police-fine` atomically with its ledger transaction.
- The relational bank row is an ATM credential record only. Its historical balance column is initialized to zero,
  never read, and has no mutation API.
- Historical relational Housing, organization-bank and police-fine schema may remain in old EF migrations until the
  pre-release database baseline is squashed, but runtime code cannot read or mutate it.

There are no rehearsal commands, provenance bindings, legacy/common authority toggles, dual reads or dual writes.
Mapper-owned `city-zones.yml` identities are registered directly into the canonical property registry when a round
starts.

## Consequences

- Every gameplay caller observes one balance and one property state.
- New databases start in the final architecture instead of manufacturing legacy rows and immediately converting them.
- A schema-baseline squash may physically remove inert historical columns/tables without any gameplay migration.
- Rollback is ordinary database backup/restore of the canonical persistence foundation; it is not reconciliation
  between competing authorities.
