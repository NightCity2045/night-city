// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Agent.Components;
using Content.Shared.Access.Components;
using Content.Shared.PDA;
using Robust.Shared.Containers;

namespace Content.Server._NC.Agent;

/// <summary>
/// Applies Night City-facing names to the vanilla PDA and its contained vanilla ID card.
/// </summary>
public sealed partial class NCAgentSystem : EntitySystem
{
    [Dependency] private MetaDataSystem _metadata = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCAgentComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<NCAgentComponent, EntInsertedIntoContainerMessage>(OnItemInserted);

        InitializePersistence();
        InitializeCalls();
        InitializeCommunication();
        InitializeLocks();
        InitializeCity();
        InitializeMissions();
        InitializeContracts();
        InitializeTraumaTeam();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        // Only the small active-call set is inspected; this never scans world entities.
        UpdateCallTimeouts();
        UpdateCommunicationTimeouts();
        UpdateAgentLocks();
        UpdateMapTracking();
    }

    private void OnMapInit(Entity<NCAgentComponent> ent, ref MapInitEvent args)
    {
        RenameAgent(ent);

        if (TryComp<PdaComponent>(ent, out var pda) && pda.ContainedId is { } idCard)
            RenameVinCard(idCard, ent.Comp);
    }

    private void OnItemInserted(Entity<NCAgentComponent> ent, ref EntInsertedIntoContainerMessage args)
    {
        if (args.Container.ID != PdaComponent.PdaIdSlotId || !HasComp<IdCardComponent>(args.Entity))
            return;

        RenameVinCard(args.Entity, ent.Comp);
    }

    private void RenameAgent(Entity<NCAgentComponent> ent)
    {
        // The Agent is still the original PDA entity; only its user-facing name changes.
        _metadata.SetEntityName(ent, Loc.GetString(ent.Comp.AgentName));
    }

    private void RenameVinCard(EntityUid card, NCAgentComponent agent)
    {
        // Keep IdCardComponent, access data, and theft behavior unchanged.
        _metadata.SetEntityName(card, Loc.GetString(agent.VinCardName));
    }
}
