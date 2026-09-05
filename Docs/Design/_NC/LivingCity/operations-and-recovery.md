# Living City operations and recovery

> Partially historical. Physical GPT A-Life v3 supersedes all materialization and legacy cognition operations below;
> the active-world clock, durable aggregate, journal, backup and versioned recovery rules remain current.

All gameplay deadlines use `NCActiveWorldTime`. Round shutdown freezes the clock; startup restores queues from durable
snapshots without wall-clock catch-up. The F7 **Living City** tab is the supported monitoring and corrective boundary;
direct database edits and console-command administration are unsupported.

Before release, run the content audit, server build, integration suite, deterministic simulation scenarios, and a
backup/restore rehearsal. Inspect outbox exhaustion, reconciliation issues, scheduler lag, materialization pressure,
service queues, incidents, and neural circuit state in F7. Corrections require server-issued confirmation tokens and
are written to the admin audit log.

If recovery finds an invariant violation, keep the affected aggregate unavailable, export its journal and snapshots,
and repair it through a versioned domain command. Never delete a journal row or advance active-world time manually.
