# NCMapCoordinates mapping contract

`NCMapCoordinates` identifies a position as a stable Z-network GUID, map-space X/Y, and a logical Z depth.
It deliberately contains no `EntityUid` or `MapId`.

## Assigning a network ID

Every persistent `NCStationZLevels` component and `zMap` prototype must define `networkId`:

```yaml
- type: NCStationZLevels
  networkId: 4ca02a1c-5e73-48d7-af28-3f6ef5df8271
```

Generate the GUID once with the server mapping command:

```text
nc-znetwork-new-id
```

The command prints a complete `networkId: ...` line ready to paste into YAML. An external website or GUID generator is not
required. Commit the generated ID with the map configuration.
Never change or reuse a published ID: saved characters, objects, and apartments will refer to it.

Copying a map into a separate city network requires a new GUID. Multiple floors of the same vertical network share one GUID
and are distinguished by their integer Z depth.

Integration tests scan every `NCStationZLevels` and `zMap` definition and reject empty or duplicate IDs. Runtime registration
also rejects duplicate IDs, so invalid networks cannot silently overwrite each other.

## Runtime behavior

The runtime Z-network entity and every map entity may receive different `EntityUid` values after a restart.
The coordinate resolver uses the stable GUID and logical depth to find the currently loaded map.
If a floor is unloaded, resolution returns `false` until it is loaded again; the stored coordinate remains valid.

X/Y are map-space coordinates. Entities on translated, rotated, or moving grids are transformed into map space when their
coordinates are captured. This records where the entity is at that moment; it does not permanently attach the coordinate to
the moving grid.

District, street, building, room, and apartment names are not part of `NCMapCoordinates`. The city zone system will resolve
those semantic locations from the stable coordinates in a later stage.
