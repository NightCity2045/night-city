# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

ent-NCAVLifter = AV lift fan
    .desc = A high-output ducted fan that keeps an AV in the air.
ent-NCAVLifterDebug = AV lift fan
    .suffix = DEBUG
    .desc = A debug lift fan with unlimited local power.
ent-NCAVPilotConsole = AV pilot console
    .desc = An autonomous flight console for piloting an AV.

nc-av-console-title = AV Flight Console
nc-av-console-mode-flight = FLIGHT
nc-av-console-mode-navigation = NAV
nc-av-console-header = NIGHT CITY AERODYNE // FLIGHT DATA
nc-av-console-channel = AVIONICS BUS 01
nc-av-console-altitude = INTERLEVEL ALT
nc-av-console-altitude-value = { $height } LVL
nc-av-console-vertical-speed = VERTICAL RATE
nc-av-console-vertical-speed-value = { $speed } LVL/S
nc-av-console-lift = LIFT CAPACITY
nc-av-console-lift-value = { $available } / { $required } kN
nc-av-console-controls = PAGE UP ASCEND // PAGE DOWN DESCEND
nc-av-console-status-grounded = GROUND LOCK
nc-av-console-status-hovering = STABLE HOVER
nc-av-console-status-ascending = ASCENDING
nc-av-console-status-descending = DESCENDING
nc-av-console-status-lift-critical = LIFT CRITICAL
nc-av-console-status-landing-blocked = LANDING BLOCKED
nc-av-console-warning-insufficient-lift = WARNING // LIFT BELOW AIRFRAME WEIGHT
nc-av-console-warning-landing-blocked = WARNING // SURFACE OR OBSTACLE PREVENTS TOUCHDOWN
