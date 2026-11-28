// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared.Popups;
using Content.Shared.Wires;

namespace Content.Shared._NC.Structures;

/// <summary>
/// The service panel cannot be opened, so the wiring cannot be hacked and the frame cannot be stripped by hand.
/// Only an RCD takes such a structure apart.
/// </summary>
[RegisterComponent]
public sealed partial class NCSealedWiresPanelComponent : Component;

public sealed class NCSealedWiresPanelSystem : EntitySystem
{
    [Dependency] private SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCSealedWiresPanelComponent, AttemptChangePanelEvent>(OnAttemptChangePanel);
    }

    private void OnAttemptChangePanel(Entity<NCSealedWiresPanelComponent> ent, ref AttemptChangePanelEvent args)
    {
        // Closing an already open panel stays allowed, so a door left open by a mapper or an admin can be shut.
        if (args.Cancelled || args.Open)
            return;

        args.Cancelled = true;
        if (args.User is { } user)
            _popup.PopupClient(Loc.GetString("nc-sealed-panel"), ent, user);
    }
}
