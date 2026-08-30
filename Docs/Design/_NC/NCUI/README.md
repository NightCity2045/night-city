<!-- SPDX-FileCopyrightText: 2026 Astro -->
<!-- SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0 -->
<!-- SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md. -->

# NCUI Framework

NCUI is the production UI foundation for interactive Night City interfaces. It stays on top of Robust UI: windows, layout, focus, text input and BUI remain native, while large interactive scenes and collections use lightweight indexed data.

All original framework code lives under `_NC/NCUI`, not under `UserInterface/Framework`:

- `Content.Client/_NC/NCUI`: surfaces, controls, virtualization, presenters, style and diagnostics;
- `Content.Shared/_NC/NCUI`: serializable session, snapshot, patch, action and continuous-input contracts;
- `Content.Server/_NC/NCUI`: authority, validation, rate limits, replication, leases and shared sessions.

The framework contains no surgery, weapon, vehicle, CitiNet or netrunning rules. A domain owns its semantic model and typed message subclasses.

## Choosing the UI level

Use ordinary Robust controls for windows, buttons, input fields, forms and small lists. Use `VirtualListControl`, `VirtualGridControl`, `VirtualTableControl` or `VirtualTreeControl` when the data set is large but only a small viewport is visible. Use `InteractiveSurfaceControl` or one of its specialized controls when thousands of independently drawn and hit-tested objects form a scene.

Never make one Robust `Control` or ECS entity per surface object.

## Interactive surface

`InteractiveSurfaceControl` owns one `UiSurfaceDocument` and the following shared services:

- dense `UiNodeStore` with stable `UiNodeId` values;
- incrementally updated `UniformGridSpatialIndex`;
- culled and cached `UiRenderQueue`;
- primitive `UiVisualRegistry` and exact `UiHitShapeRegistry`;
- `UiInteractionRouter`, pointer capture and event bubbling;
- `UiSelectionService`, typed `UiDragDropService` and `UiToolController`;
- central `UiAnimationScheduler` that visits active animations only;
- allocation, hit-test, update, draw and network metrics.

Surface geometry is stored in virtual UI units. `UiViewportTransform` exposes pan, zoom, viewport bounds, screen/surface conversion, Robust-local conversion and physical-pixel snapping. The renderer handles rectangles, polygons, lines, polylines, arcs, sprites, nine-patches, text, icons and entity previews. Composite domain objects are groups of these primitives.

Visual and hit-shape handles are reference-counted and recycled after the last node releases them. Updating a wire or stroke replaces the definition behind its stable handle instead of growing a registry every pointer move.

## Input and tools

Robust pointer and keyboard input is normalized into `UiPointerEventType`. Node handlers may handle an event, bubble it to the parent, capture the logical pointer or release capture. The surface additionally publishes pan, zoom and selection lifecycle events.

Domain tools implement `IUiSurfaceTool`. Tool IDs are stable and non-localized; displayed names are localized by the presenter. A tool consumes the same pointer pipeline as direct interaction and can return `Ignored`, `Handled`, `CapturePointer`, `Complete` or `Cancel`.

Drag payloads implement `IUiDragPayload`; no anonymous `object`, JSON or dictionary payload is allowed. `IUiDropTarget` provides local preview only. A successful preview transitions the source to `PendingServer`; the authoritative domain response resolves it to normal or error state.

For native text focus, IME and keyboard behavior, place `SurfaceTextEditorHost` over the surface and call `Begin(...)` for a lightweight node. Tooltips, context actions, inspectors, drag ghosts and minimaps likewise remain real overlay controls.

## Virtualized collections

Virtual controls use fixed-height rows, overscan and `RecycledControlPool`. Selection and scroll anchors are stable data keys, never row-control identity. `VirtualCollectionView<T, TKey>` owns filtering, sorting and selection. `VirtualTreeView<T, TKey>` owns expansion and produces only the visible flattened branch list.

Reconfigure a row through its binder; do not attach per-item subscriptions that survive recycling. A 50,000-record view should normally keep only the visible rows plus overscan alive.

## Client state flow

The supported flow is strictly typed:

```text
server domain state
  -> NcUiSnapshotMessage / NcUiPatchMessage subclass
  -> NcUiClientSession<TSnapshot, TPatch>
  -> presenter / view model
  -> Robust controls and surface document mutations
```

`UiPatchApplier` accepts only an uninterrupted revision chain. A gap raises `ResyncRequested`; old patches and wrong-session patches are not applied. `UiPredictionBuffer<TState>` rolls reversible presentation predictions back around authoritative changes and retains accepted predictions until their acknowledged revision arrives.

Construct a client session with the surface diagnostics to enable metrics and fault injection:

```csharp
var session = new NcUiClientSession<MySnapshot, MyPatch>(
    surface.DebugSettings,
    surface.Metrics);
session.SnapshotReceived += presenter.ApplySnapshot;
session.PatchReceived += presenter.ApplyPatch;
session.ResyncRequested += SendToServer;
```

Call `session.Update(timing.CurTime)` from the owning BUI while delayed-response injection is enabled. Pass encoded byte counts to `Handle(...)` and `RecordOutbound(...)` when the domain transport exposes them.

## Server action flow

Every concrete command subclasses `NcUiActionMessage`. The server first calls `NcUiValidationSystem.Validate(...)` with its owner, UI key, actor, required framework permission, traffic class and payload size. This proves only the common envelope:

- owner and actor still exist;
- the BUI is open for that actor and key;
- session, participant, revision and sequence are valid;
- permission, payload and rate limits pass.

The domain system must then validate gameplay facts: item existence and ownership, target existence, access, distance, actor state, compatible tool/layer and the physical possibility of the operation. Client visibility, enabled state and drop preview are never evidence.

For continuous paths use `ValidateContinuousUpdate(...)`, then `TryAppendOperation(...)`. Compressed points, quantum, segment sequence, point count, segment length, total length, speed and client interval are bounded before a domain computes consequences. `UiStrokeBatcher` emits at 10–20 Hz rather than once per pointer event.

`NcUiReplicationSystem.PublishPatch(...)` is the sole framework revision advancement boundary. It validates the patch envelope, keeps bounded history and broadcasts only to open participants. Resync first attempts contiguous history replay; otherwise the domain sends a fresh snapshot using the framework snapshot helpers.

## Shared sessions

`NcUiSession` owns participants, framework permissions, short object leases and active operations. The domain chooses an arbitration mode per semantic object:

- `Exclusive`: `TryBeginObjectMutation(...)` acquires a lease or returns conflict;
- `Optimistic`: the authoritative revision decides the winner;
- `AppendOnly`: independent records may be added concurrently.

Lease duration is bounded by session options. Expired leases are removed without per-frame allocations. Player detach/disconnect releases leases, cancels operations, clears rate buckets and emits patches. Deleting the owner closes the session. Snapshots and patches include participants, permission changes, leases, remote selection and compressed remote-operation updates.

## StyleNightCity

`NightCityStylesheet` derives from current `CommonStylesheet` and consumes the common paletted sheetlets. NCUI sheetlets add surface, virtualization, context, inspector and typography rules. It is globally registered as `SheetNightCity` while the vanilla Nanotrasen sheet remains the application default.

Opt in per XAML root:

```xml
Stylesheet="NightCity"
```

Use `NCSpacing`, `NCRadius`, `NCBorder`, `NCFont`, `NCFontSize`, `NCPanel`, `NCText`, `NCAccent`, `NCDanger`, `NCWarning`, `NCSuccess`, `NCSelection` and `NCMotion`. `NcUiDeviceThemes` supplies Street, NCPD, Militech, Trauma Team, Biotechnica, Industrial and Black Market device palettes without changing layout or behavior.

## Laboratory and diagnostics

Run `ncui_lab`. The window contains 50,000 virtual records, 10,000 lightweight nodes, selection, dragging, drop targets, graph ports/wires, stroke tooling, minimap and inspector. The 10,000-real-Control comparison is opt-in because it intentionally exercises the expensive baseline.

Available commands:

- `ncui_debug_bounds`
- `ncui_debug_hitboxes`
- `ncui_debug_spatial`
- `ncui_debug_events`
- `ncui_debug_network`
- `ncui_debug_layout`
- `ncui_debug_allocations`
- `ncui_debug_delay`
- `ncui_debug_reject_next`
- `ncui_debug_drop_patch`

`NcUiDebugOverlay` displays session/revision, real controls, node counts, queried buckets, draw calls, active animations, timings, pending actions, network KiB/s and allocations. The laboratory also exposes revision-gap resync and a simulated second participant.

## Performance invariants

- no LINQ or object creation in normal draw, hit-test or animation loops;
- render culling/sorting is reused until geometry, visibility, z-order or camera changes;
- visual hover does not invalidate Robust measure/arrange;
- spatial entries update incrementally when bounds cross cells;
- only active animations update;
- virtual rows are rebound and rearranged, not recreated;
- ordinary patches mutate the affected view-model/document records and never rebuild the complete UI;
- continuous input is filtered, bounded, compressed and packetized.

The laboratory targets remain 10,000 logical nodes, 1,000–2,000 visible nodes, 500–1,000 interactive nodes, 50,000 collection records and zero steady-state allocations in draw/hit-test after buffers have warmed.

## Reusable controls

The shipped library includes `InteractiveSurfaceControl`, `PanZoomSurfaceControl`, `NodeGraphControl`, `WireConnectionControl`, `SlotBoardControl`, `StrokeInputControl`, `TimelineControl`, `LayeredBodyControl`, `MapSurfaceControl`, `CurveEditorControl`, all four virtual collection controls, `RichTooltipHost`, `ContextActionHost`, `SelectionInspector`, `DragGhostLayer`, `SurfaceTextEditorHost`, `SurfaceMinimap` and `NcUiDebugOverlay`.

Future domain interfaces should extend these controls and typed contracts instead of creating a second surface, drag/drop, session or patch architecture.
