# Living City architecture decisions

This directory records accepted architectural decisions for the Night City 2045 persistent city and A-Life implementation.

Current NPC authority is Physical GPT A-Life v3 in `.rules/A-life/plan.md`. ADR-0013 and ADR-0014 are retained only as
historical v2 decisions and must not be used to restore materialization, StateTree/BDI cognition, or registry intents.

- [ADR-0001: Persistent parties and ownership](ADR-0001-persistent-parties-and-ownership.md)
- [ADR-0002: Domain action execution](ADR-0002-domain-action-execution.md)
- [ADR-0003: City connectivity identity](ADR-0003-city-connectivity-identity.md)
- [ADR-0004: Active world time](ADR-0004-active-world-time.md)
- [ADR-0005: Direct persistence authority before release](ADR-0005-direct-persistence-authority.md)
- [ADR-0006: Atomic ledger participants](ADR-0006-atomic-ledger-participants.md)
- [ADR-0007: Common property and active-world rent time](ADR-0007-common-property-and-active-rent-time.md)
- [ADR-0008: Durable physical-cash handoffs](ADR-0008-durable-physical-cash-handoffs.md)
- [ADR-0009: Directed social relationships](ADR-0009-directed-social-relationships.md)
- [ADR-0010: Canonical reputation channels](ADR-0010-canonical-reputation-channels.md)
- [ADR-0011: Structured episodic memory and deterministic consolidation](ADR-0011-structured-episodic-memory.md)
- [ADR-0012: F7 Living City administration surface](ADR-0012-f7-admin-operations.md)
- [ADR-0013: Physical AI execution and unchanged local pathfinding (superseded)](ADR-0013-physical-ai-execution.md)
- [ADR-0014: Hybrid cognition, BDI commitment and plan routing (superseded)](ADR-0014-hybrid-cognition-and-plan-routing.md)
- [ADR-0015: Social profiles, rumors, obligations and conversations](ADR-0015-social-context-rumors-obligations-and-conversations.md)
- [Operations and recovery](operations-and-recovery.md)
- [Neural Cognition Service operations](neural-cognition-operations.md)
- [Implementation status](implementation-status.md)

The current architecture is defined by `.rules/A-life/plan.md` and its linked v3 specification. The `.rules` directory
is local and ignored by Git; these ADRs remain the tracked historical record.
