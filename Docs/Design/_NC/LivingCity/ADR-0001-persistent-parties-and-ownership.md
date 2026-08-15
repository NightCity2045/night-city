# ADR-0001: Persistent parties and ownership

Status: Accepted  
Date: 2026-08-15

## Decision

Player characters, persistent NPC actors, organizations, businesses, city services, and estates keep distinct stable IDs. They are referenced in shared ownership, transaction, contract, employment, and obligation records through one discriminated `NCPartyRef`.

Role-specific fields retain role-specific names such as `Owner`, `Buyer`, `Seller`, and `Employer`. A separate `NCOwnerRef` is not introduced.

`NCCharacterId` remains backed by the existing character profile row. `NCActorId` and future non-character party IDs use stable GUIDs. `EntityUid` is never a persistent party identity.

## Consequences

- Player and NPC parties can participate in the same economy without sharing a lifecycle or raw ID type.
- Domain services must validate that a party kind is permitted for each role.
- Database models may store the discriminant and primitive ID columns because `Content.Server.Database` does not depend on `Content.Shared`.
- Runtime actor binding maps one `NCActorId` to at most one live `EntityUid` in the current session epoch.
