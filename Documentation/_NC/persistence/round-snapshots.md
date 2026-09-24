<!-- SPDX-FileCopyrightText: 2026 Astro -->
<!-- SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0 -->
<!-- SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md. -->

# Round snapshot persistence

`NCRoundSnapshotCoordinatorSystem` is the only owner of the end-of-round snapshot transaction. Domain systems
handle `NCRoundSnapshotCaptureEvent`, synchronously copy their ECS state into immutable bounded sections, and never
write those sections themselves. The coordinator validates every section, computes the manifest checksum and sends
one `CommitNCRoundSnapshotAsync` command to the database. A snapshot becomes readable only after its manifest changes
from `Staging` to `Committed` in the same serializable transaction.

The database retains three committed snapshots. Readers select sections from the newest committed manifest and may
fall back to their old aggregate table while legacy saves are being migrated. A failed capture or write prevents the
active-world close barrier from succeeding, so the server does not silently continue from a partial snapshot.

Current section owners are:

- `player-medical/character`: anatomy, physiological state and installed cyberware;
- `player-possessions/character`: equipment slots, hands and recursively contained items;
- `character-lifecycle/character`: the survival decision used for atomic profile deletion;
- `property-content/property`: contents of each deployed rented apartment;
- `city-map-delta/world`: non-wall roots of the common city map, split into bounded chunks;
- `system-metadata/world`: schema, map and active-world epoch metadata.

An irreversibly dead character is deleted inside the snapshot transaction. If other profiles exist, the selected slot
is moved to a survivor. If it was the account's final profile, the preference row is removed and the normal preference
loader creates a fresh character. Connected preference caches are reloaded before round teardown completes.

Apartment snapshots intentionally exclude `Wall` entities and permanent property shells. The next round deploys the
mapper-authored template first and overlays the saved renter contents, so destroyed apartment walls return while
furniture, containers and resident items retain their saved state.

## General city map

`NCCityMapDeltaSnapshotSystem` captures each non-wall root on the semantic city map as a bounded generic entity graph.
Its position uses `NCMapCoordinates` (network GUID, Z and local X/Y), so runtime `MapId` and `EntityUid` values are not
durable identities. Actors, mobs, apartment deployments, resident property items and player-owned subtrees are excluded
because their domain systems own those records.

At the next active-world start, the mapper source loads first. The system validates and deserializes the entire saved
overlay before changing the baseline. It then removes all eligible baseline non-wall roots and places the saved roots
at their semantic coordinates. Missing objects therefore stay deleted and dynamically spawned objects return. Walls
are never removed or restored by this section, so every wall starts from the mapper-authored state even if it was
destroyed in the previous round. A database, format, coordinate or entity-graph error leaves the clean baseline intact.

The manifest map prototype ID prevents applying a city's overlay to a different map. Changing the contents of a map
without changing that ID still requires an explicit migration or discarding the old snapshots; there is not yet a
content hash for mapper revisions.

Financial transfers, purchases, rent mutations and other externally meaningful transactions remain immediate durable
writes. They are journal operations rather than reconstructable world snapshots and must not wait for round end.
