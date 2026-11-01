<!-- SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0 -->
# Private security journal

Bind owned-camera, owned-door and journal-data to explicitly approved owned devices. This ordinary META/MUI app retains a bounded private rolling text log and reloads it from the FileStore on start. Its file is user data, separate from the protected native NET evidence journal. Camera confidence below0.5 and stale events are ignored. The fixture checks exact read and atomic write traces. Native sensor visibility and physical access remain authoritative.

