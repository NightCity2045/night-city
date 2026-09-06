// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Agent.Components;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.Identity;
using Content.Shared.PDA;

namespace Content.Server._NC.Agent;

public sealed partial class NCAgentSystem
{
    [Dependency] private NCPlayerContractSystem _contracts = default!;

    private void InitializeContracts()
    {
        SubscribeLocalEvent<NCAgentComponent, NCAgentContractCreateMessage>(OnContractCreate);
        SubscribeLocalEvent<NCAgentComponent, NCAgentContractActionMessage>(OnContractAction);
        SubscribeLocalEvent<NCAgentComponent, NCAgentContractsPageMessage>(OnContractsPage);
    }

    private async void OnContractCreate(EntityUid uid, NCAgentComponent component, NCAgentContractCreateMessage args)
    {
        if (!CanUseContracts(uid, args.Actor, out _) || !_processing.Add((uid, args.Actor)))
            return;
        try
        {
            var success = await _contracts.TryCreateAsync(args.Actor, args);
            await SendContractsAsync(uid, args.Actor, true, notice: success ? "nc-contract-created" : "nc-contract-failed");
        }
        catch (Exception exception) { HandleDatabaseError(uid, args.Actor, "create player contract", exception); }
        finally { _processing.Remove((uid, args.Actor)); }
    }

    private async void OnContractAction(EntityUid uid, NCAgentComponent component, NCAgentContractActionMessage args)
    {
        if (!CanUseContracts(uid, args.Actor, out _) || !_processing.Add((uid, args.Actor)))
            return;
        try
        {
            var success = await _contracts.TryActAsync(args.Actor, args);
            await SendContractsAsync(uid, args.Actor, true, notice: success ? "nc-contract-updated" : "nc-contract-failed");
        }
        catch (Exception exception) { HandleDatabaseError(uid, args.Actor, "update player contract", exception); }
        finally { _processing.Remove((uid, args.Actor)); }
    }

    private async void OnContractsPage(EntityUid uid, NCAgentComponent component, NCAgentContractsPageMessage args)
    {
        await SendContractsAsync(uid, args.Actor, args.History, args.Cursor);
    }

    private bool CanUseContracts(EntityUid uid, EntityUid user, out NCCharacterId owner)
    {
        owner = default;
        return TryGetView(uid, user, out _, out var view) && view.ActiveApp == NCAgentApp.Contracts &&
            TryGetCharacterId(user, out owner) && owner == view.Owner && CanUseCitiNetService(owner, out _);
    }

    private async Task SendContractsAsync(EntityUid uid, EntityUid user, bool history = true,
        string? cursor = null, string notice = "")
    {
        try
        {
            if (!CanUseContracts(uid, user, out var owner))
            {
                if (!Deleted(uid) && TryGetView(uid, user, out _, out _))
                    _ui.ServerSendUiMessage(uid, PdaUiKey.Key,
                        new NCAgentContractsUpdateMessage([], 0, history, null, "nc-contract-unavailable"), user);
                return;
            }
            var page = await _contracts.GetPageAsync(owner, history, cursor);
            // A delayed database response must never disclose the previous owner's history after locking/switching.
            if (CanUseContracts(uid, user, out var current) && current == owner)
                _ui.ServerSendUiMessage(uid, PdaUiKey.Key,
                    new NCAgentContractsUpdateMessage(page.Entries, owner.Value, history, page.Cursor, notice), user);
        }
        catch (Exception exception) { HandleDatabaseError(uid, user, "read player contracts", exception); }
    }
}
