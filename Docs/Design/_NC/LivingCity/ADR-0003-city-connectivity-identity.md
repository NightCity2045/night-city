# ADR-0003: City connectivity identity

Status: Accepted  
Date: 2026-08-15

## Decision

`city-zones.yml`, `NCZoneId`, and the existing Z-network model remain the semantic geography authority.

Ordinary doors, open passages, and other locally discovered portals have runtime identity only. They are rebuilt from map state and navigation data after load; persistent actors do not store their `EntityUid` or a durable door ID.

Stable link identity is limited to:

- authored vertical-link markers connecting floors or maps inside one Z-network;
- entrance markers belonging to a persistent dynamic deployment.

Metro, bus routes, permanent inter-map city transport, corporate tunnels, and permanent dungeon links are outside the current product scope. Dynamic dungeon and mission deployments may register entrances while active.

## Consequences

- Mapper work remains zone geometry plus ordinary physical map entities.
- A restart may invalidate a local portal route; the actor replans from stable semantic location.
- Vertical and deployment link IDs derive from stable marker/deployment data rather than runtime entity identity.
- The connectivity index is a derived server cache, not a second city geography database.
