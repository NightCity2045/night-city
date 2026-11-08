// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Optics;
using Content.Shared._NC.Medical.Cyberware.Visuals;
using Content.Shared.Body;
using Robust.Shared.Containers;

namespace Content.Server._NC.Medical.Cyberware.Optics;

public sealed partial class NCCyberOpticsSystem
{
    [Dependency] private SharedVisualBodySystem _visualBody = default!;
    private void InitializeAppearance()
    {
        SubscribeLocalEvent<NCCyberEyeComponent, NCGetOrganVisualAppearanceEvent>(OnAppearance);
        SubscribeLocalEvent<NCCyberEyeComponent, EntInsertedIntoContainerMessage>(OnAppearanceInserted);
        SubscribeLocalEvent<NCCyberEyeComponent, EntRemovedFromContainerMessage>(OnAppearanceRemoved);
    }
    private void OnAppearanceInserted(Entity<NCCyberEyeComponent> ent, ref EntInsertedIntoContainerMessage args) => _visualBody.TryRefreshOrganAppearance(ent.Owner);
    private void OnAppearanceRemoved(Entity<NCCyberEyeComponent> ent, ref EntRemovedFromContainerMessage args) => _visualBody.TryRefreshOrganAppearance(ent.Owner);
    private void OnAppearance(Entity<NCCyberEyeComponent> ent, ref NCGetOrganVisualAppearanceEvent args)
    {
        args.Data.Color = Color.Cyan;
        args.Data.Shader = null;
        if (!TryComp<NCCyberwareAssemblyComponent>(ent, out var assembly) || !assembly.Parts.TryGetValue("NCOpticCosmeticSlot", out var socket) ||
            socket.ContainedEntity is not { } part || !TryComp<NCOpticModuleComponent>(part, out var cosmetic) || !cosmetic.Cosmetic) return;
        args.Data.Color = cosmetic.IrisColor;
        if (cosmetic.Glow) args.Data.Shader = "unshaded";
    }
}
