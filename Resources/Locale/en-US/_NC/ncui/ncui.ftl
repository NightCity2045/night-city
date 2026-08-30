# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

ncui-lab-title = NCUI Laboratory
ncui-lab-metrics-waiting = Metrics are warming up…
ncui-lab-tool-stroke = Stroke tool
ncui-lab-tool-selection = Selection tool
ncui-lab-baseline-enable = Enable 10,000-Control baseline
ncui-lab-baseline-disable = Return to NCUI surface
ncui-lab-simulate-user = Simulate second participant
ncui-lab-remove-user = Remove second participant
ncui-lab-simulate-resync = Simulate revision gap
ncui-lab-virtual-list-heading = 50,000 virtual records
ncui-lab-inspector-heading = Selection inspector
ncui-lab-inspector-empty = Select a surface object.
ncui-lab-event-empty = Enable ncui_debug_events to inspect routing.
ncui-lab-record = Record {$index}
ncui-lab-inspector-value = Node {$id} at {$x}, {$y}; selected: {$count}
ncui-lab-event-value = {$type} → node {$id}; handled: {$handled}
ncui-lab-resync-value = Synthetic gap detected; snapshot resync requested at revision {$revision}.
ncui-lab-metrics = Controls {$controls} | Nodes {$nodes} | Visible {$visible} | Interactive {$interactive} | Recycled rows {$rows} | Draw {$draw} ms | Hit {$hit} ms | Alloc {$allocations} B | Revision {$revision}

ncui-debug-enabled = {$command}: enabled
ncui-debug-disabled = {$command}: disabled
ncui-debug-overlay-metrics =
    Session {$session}; revision {$revision}
    Nodes {$nodes} / {$visible} visible / {$interactive} interactive
    Buckets {$buckets}; draw calls {$drawCommands}; animations {$animations}
    Hit {$hit} ms; update {$update} ms; draw {$draw} ms; patch {$patch} ms
    Pending {$pending}; network {$networkIn}/{$networkOut} KiB/s; allocations {$allocations} B/frame

cmd-ncui_lab-desc = Opens the NCUI performance and interaction laboratory.
cmd-ncui_debug_bounds-desc = Toggles logical node bounds.
cmd-ncui_debug_hitboxes-desc = Toggles interactive hit shapes.
cmd-ncui_debug_spatial-desc = Toggles occupied spatial grid cells.
cmd-ncui_debug_events-desc = Toggles routed NCUI event inspection.
cmd-ncui_debug_network-desc = Toggles NCUI network diagnostics.
cmd-ncui_debug_layout-desc = Toggles NCUI layout diagnostics.
cmd-ncui_debug_allocations-desc = Toggles per-frame NCUI allocation measurement.
cmd-ncui_debug_delay-desc = Delays inbound NCUI responses for fault-injection testing.
cmd-ncui_debug_reject_next-desc = Forces the next NCUI action through the local rejection path.
cmd-ncui_debug_drop_patch-desc = Drops the next NCUI patch to test gap detection and resync.
