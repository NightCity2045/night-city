// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Agent.Shards.Notes;
using Content.Shared.Administration.Logs;
using Content.Shared.CartridgeLoader;
using Content.Shared.Database;

namespace Content.Server._NC.Agent.Shards;

/// <summary>
/// Validates and applies all NOTES changes on the server so a client cannot exceed YAML-defined limits.
/// </summary>
public sealed partial class NCAgentNotesSystem : EntitySystem
{
    [Dependency] private CartridgeLoaderSystem _cartridges = default!;
    [Dependency] private ISharedAdminLogManager _adminLog = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCAgentNotesComponent, CartridgeUiReadyEvent>(OnUiReady);
        SubscribeLocalEvent<NCAgentNotesComponent, CartridgeMessageEvent>(OnUiMessage);
    }

    private void OnUiReady(Entity<NCAgentNotesComponent> ent, ref CartridgeUiReadyEvent args)
    {
        UpdateUi(ent, args.Loader);
    }

    private void OnUiMessage(Entity<NCAgentNotesComponent> ent, ref CartridgeMessageEvent args)
    {
        if (args is not NCAgentNotesUiMessage message)
            return;

        var body = message.Body.Trim();
        var changed = message.Action switch
        {
            NCAgentNotesAction.Add => TryAdd(ent, body),
            NCAgentNotesAction.Edit => TryEdit(ent, message.NoteId, body),
            NCAgentNotesAction.Delete => TryDelete(ent, message.NoteId),
            _ => false,
        };

        if (!changed)
            return;

        Dirty(ent);
        _adminLog.Add(
            LogType.PdaInteract,
            LogImpact.Low,
            $"{ToPrettyString(args.Actor)} performed {message.Action} in Agent NOTES on {ToPrettyString(ent)}");
        UpdateUi(ent, GetEntity(args.LoaderUid));
    }

    private static bool TryAdd(Entity<NCAgentNotesComponent> ent, string body)
    {
        if (string.IsNullOrWhiteSpace(body) ||
            body.Length > ent.Comp.MaximumNoteLength ||
            ent.Comp.Entries.Count >= ent.Comp.MaximumNotes)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        ent.Comp.Entries.Add(new NCAgentNoteEntry(Guid.NewGuid(), body, now, now));
        return true;
    }

    private static bool TryEdit(Entity<NCAgentNotesComponent> ent, Guid id, string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > ent.Comp.MaximumNoteLength)
            return false;

        var index = ent.Comp.Entries.FindIndex(note => note.Id == id);
        if (index < 0)
            return false;

        var previous = ent.Comp.Entries[index];
        if (previous.Body == body)
            return false;

        ent.Comp.Entries[index] = new NCAgentNoteEntry(previous.Id, body, previous.CreatedAt, DateTime.UtcNow);
        return true;
    }

    private static bool TryDelete(Entity<NCAgentNotesComponent> ent, Guid id)
    {
        return ent.Comp.Entries.RemoveAll(note => note.Id == id) > 0;
    }

    private void UpdateUi(Entity<NCAgentNotesComponent> ent, EntityUid loader)
    {
        // Copy the list so later component mutations cannot alter an already queued UI state.
        _cartridges.UpdateCartridgeUiState(loader, new NCAgentNotesUiState([.. ent.Comp.Entries]));
    }
}
