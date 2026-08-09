# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

ent-NCMissionDynamicPointMarker = mission dynamic point
    .desc = Marks a data-driven runtime spawn pool inside a public mission source template.
ent-NCMissionEvacuationPointMarker = mission evacuation point
    .desc = Marks the safe world position used before a timed public mission disappears.
ent-NCMissionConsole = public mission terminal
    .desc = Deploys a timed physical mission directly into the shared Night City world.
ent-NCMissionConsoleCircuitboard = public mission terminal board
    .desc = A computer board for a Night City public mission terminal.

nc-zone-template-mission-layout-8x8 = Occupied warehouse mission (8×8, two floors)

nc-mission-console-title = Night City Missions
nc-mission-console-heading = Public city mission
nc-mission-console-public-warning = This mission appears in the shared world. Anyone may enter, help, interfere, or extract loot.
nc-mission-console-mission = Mission:
nc-mission-console-slot = Mission slot ID:
nc-mission-console-state = State:
nc-mission-console-time-left = Time remaining:
nc-mission-console-time-inactive = Not deployed
nc-mission-console-time-closing = Evacuation and cleanup in progress
nc-mission-console-time-value = {$time}
nc-mission-console-evacuation-warning = Warning: the mission is approaching its hard cleanup deadline.
nc-mission-console-start = Deploy public mission
nc-mission-console-finish = End test mission
nc-mission-console-finish-confirm = Request early cleanup? Players inside must leave before ordinary cleanup can finish.

nc-mission-console-state-requested = requested
nc-mission-console-state-preparing = preparing both floors
nc-mission-console-state-ready = ready
nc-mission-console-state-active = active
nc-mission-console-state-completed = completed
nc-mission-console-state-failed = failed
nc-mission-console-state-aborted = aborted
nc-mission-console-state-closing = closing
nc-mission-console-state-cleaning = cleaning
nc-mission-console-state-closed = available

nc-mission-console-result-no-character = No active persistent character was found.
nc-mission-console-result-unavailable = The mission slot or definition is unavailable.
nc-mission-console-result-started = The public mission is being deployed on both city levels.
nc-mission-console-result-failed = The mission operation failed.
nc-mission-console-result-not-authorized = Only the character who initiated this test run may request early cleanup.
nc-mission-console-result-finish-requested = Early cleanup requested. The zone will restore after external occupants leave.
