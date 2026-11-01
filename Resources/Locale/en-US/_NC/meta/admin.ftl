# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
nc-meta-admin-description = Inspect and control META execution without reading private source.
nc-meta-admin-help = ncmeta status/repro [hostNetEntity]; enabled true/false; stop process/device/principal <id>; block/unblock/quarantine <hash>; revoke <installation-id> <reason>
nc-meta-admin-summary = META enabled: {$enabled}; valid policy: {$valid}; processes: {$processes}; hosts: {$hosts}; queued commands: {$pending}; blocked hashes: {$blocked}.
nc-meta-admin-stopped = Stopped {$count} META processes. Native manual controls remain available.
nc-meta-admin-pending = Administrative revocation is pending durable confirmation.
nc-meta-admin-revoked = Installation capabilities were durably revoked.
nc-meta-admin-policy-saved = Runtime policy applied and configuration save requested. Check the server log before restarting.
nc-meta-admin-policy-unsaved = Runtime policy applied, but server configuration could not be saved. Save it before restarting.
