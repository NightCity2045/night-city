# Night City zones

City zones add semantic names such as district, street, building, and apartment to stable `NCMapCoordinates`.
They are authored as `ncZoneSet` prototypes, one set per persistent Z-network. Zone geometry is server-only and
ordinary clients do not receive the city's complete apartment layout.

## Hierarchy

The standard hierarchy is:

```text
District -> Sector -> Street -> Building -> Apartment
```

Kinds and their allowed parents and geometry are data-driven in
`Resources/Prototypes/_NC/City/zone_kinds.yml`. A logical zone has one persistent GUID and may contain several
geometry fragments. Consequently, a district can contain disconnected territory and one building can cover multiple
floors without inventing a separate identity for every fragment.

Generate a zone GUID with:

```text
nc-zone-new-id
```

Never change a published zone GUID. Future saved apartments, local networks, and ownership records will refer to it.

At runtime the same GUID is exposed through a strongly typed identity matching the zone kind:
`NCDistrictId`, `NCSectorId`, `NCStreetId`, `NCBuildingId`, or `NCApartmentId`. These are wrappers around the
existing `NCZoneId`, not additional IDs. This prevents gameplay code from accidentally using a street as an apartment
while keeping one source of persistent identity in maps and databases.

## Geometry

- `Polygon` is a map-space polygon on one logical Z depth.
- `Volume` extrudes a map-space polygon through an inclusive `minZ`/`maxZ` range.
- `global: true` makes polygonal geometry apply on every current and future floor in the same Z-network. It is
  intended for district, sector, and street boundaries that do not change vertically.
- `TileMask` stores exact occupied map tiles in sparse 32 by 32 chunks. Each row is a bit field where bit zero is
  the chunk origin's X coordinate.

Example:

```yaml
- type: ncZoneSet
  id: WatsonZones
  networkId: 4ca02a1c-5e73-48d7-af28-3f6ef5df8271
  zones:
  - id: 78fcc966-12cc-4a15-ae2d-d2f65bbd09c8
    kind: Apartment
    parent: ddf2ded2-d9ac-4201-936f-1badfc179a89
    name: Clinic reception
    geometry:
    - kind: TileMask
      chunks:
      - z: 0
        origin: 128,64
        rows: [15, 15, 15]
```

## Mapper workflow

1. Enter the target Z-network with an active administrator account that has `Mapping` permission.
2. Press `B`, open `City Zones`, and enable the zone overlay.
3. Create zones from broadest to most specific: district, sector, street, building, then apartments.
4. Select a zone and use `Draw shape` or `New tile mask` directly on the current floor.
   Enable `Global Z` before drawing when the same outline must apply to every floor. For buildings and apartments, leave it
   disabled and enter an inclusive `Min Z` and `Max Z`. Equal values save a single-floor polygon; different values save
   an inclusive volume. The loaded-floor selectors are shortcuts, while the numeric fields also accept planned floors
   that have not been created yet.
5. Validate the draft, fix listed errors, and save the Z-network. The zone set is written beside its maps as
   `/ZNetworkSaves/<saveName>/_zones.yml`.

Shape drawing uses left click to place snapped vertices and right click to finish. A selected polygon vertex can be
moved, deleted, or followed by a
new vertex placed with the next map click. Select existing polygonal geometry and use `Apply Z scope` to change it
between one floor, an inclusive floor range, and global coverage without redrawing its outline. Tile masks always remain
bound to explicit floors.

Tile masks use 1x1 through 9x9 brushes. Holding the primary placement button batches the complete stroke into one
server operation and one undo entry.

An apartment is one gameplay property and may cover several physical rooms. Draw its common outline manually with a
polygon or volume, or paint an exact tile mask when its shape is irregular. Individual bedrooms, kitchens, closets, and
corridors are not separate city zones.

## Runtime behavior

The server builds sparse 16 by 16 map-space indexes: floor-bound geometry is keyed by `NCZNetworkId` and logical Z,
while global geometry uses a separate two-dimensional index. A point query examines only the relevant cells and returns
immutable `NCZoneInfo` values; callers do not receive prototype internals or components. The global index automatically
covers floors added later without duplicating geometry per floor. Indexes rebuild when zone prototypes reload.

Validation rejects duplicate or empty IDs, missing or incompatible parents, hierarchy cycles, unsupported geometry,
invalid polygons and volumes, and malformed tile-mask chunks.

`NCCityLocationSystem.EnsureTracked(entity)` adds an opt-in networked cache. It contains one primary ID for each
data-driven context slot and updates from `MoveEvent` and explicit Z-level transitions. It has no global update loop.
When the semantic context changes, the system raises a directed `NCZoneChangedEvent` on the entity. Exact X/Y is not
cached, so ordinary movement inside one apartment does not dirty the component. Prototype reloads refresh every opted-in
cache, and one invalid zone set is skipped without disabling valid city networks.

Gameplay systems query `NCCityLocationSystem` for an entity's typed district, sector, street, building, or apartment,
then use `NCZoneSystem` to obtain immutable zone metadata. Neither API exposes cache components or prototype objects.
Typed IDs serialize as the same GUID scalar in YAML, over the network, and through
`NCCityObjectIdDatabaseCodec` for persistent storage. A loaded GUID is checked against the current zone kind before it
is accepted as a gameplay object.

District and sector prototypes support `Active`, `Warm`, and `Abstract` runtime modes:

```text
nc-zone-activity <zoneId>
nc-zone-activity <zoneId> <Active|Warm|Abstract>
```

Changing a mode raises `NCZoneActivityChangedEvent`. The zone system defines and publishes the mode; simulation
consumers decide what Active, Warm, or Abstract means for their own entities.

## Visual mapping editor

The editor is intentionally opt-in and requires the server `Mapping` permission. Full apartment geometry is never part of
normal PVS or ordinary prototype replication.

Each mapper receives an isolated server-side draft with bounded undo and redo history. Unchanged immutable zone data is
shared between history revisions, so editing one vertex does not duplicate the entire city in server memory. Normal
edits replicate only changed zones and removed IDs; a revision mismatch requests an authoritative full snapshot.
Closing and reopening `B`
preserves that draft for the current connection. `Reload prototype` explicitly discards it and requires a second
confirmation click.

The server generates persistent zone GUIDs, enforces data-driven hierarchy and geometry rules, validates all network
operations, and limits geometry and tile-patch sizes. Entity, tile, and decal placement are cleared when a zone drawing
tool starts so one click cannot invoke two mapping tools.

The overlay draws polygons, volumes, exact tile masks, pending polygon segments, and brush cursors on
the mapper's current logical floor. Global outlines are shown on every floor. All cursor positions are converted through
`NCMapCoordinates`; runtime map entity identifiers are never persisted.

`znetwork-save` writes the mapper's valid draft beside the map files as
`/ZNetworkSaves/<saveName>/_zones.yml`. `znetwork-load` validates its persistent network ID, loads the zone set, and
rebuilds the runtime spatial index, so opening the `B` panel immediately shows the restored markup. Old Z-network
saves without this sidecar remain loadable.

The Export button still writes a standalone backup beneath server user data at `/ZoneExports/<fileName>.yml`. It does
not replace the bundled `znetwork-save` workflow.

Server commands remain for ID generation, validation, inspection, activity control, and headless recovery. The old
client authoring commands were removed; normal authoring is performed through the `B` interface.
