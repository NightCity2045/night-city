// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Construction.Components;
using Robust.Shared.Prototypes;

namespace Content.Shared.Construction;

public abstract partial class SharedFlatpackSystem
{
    /// <summary>
    /// Configures a generic flatpack with a data-authored result prototype without requiring a machine board.
    /// </summary>
    public bool TrySetFlatpackResult(Entity<FlatpackComponent?> ent, EntProtoId prototype)
    {
        if (!Resolve(ent, ref ent.Comp) || !ProtoMan.Resolve(prototype, out var resultPrototype))
            return false;

        ent.Comp.Entity = prototype;
        Dirty(ent, ent.Comp);

        // Match machine-created flatpacks so the purchased package clearly identifies its contents.
        var metadata = MetaData(ent);
        _metaData.SetEntityName(ent, Loc.GetString("flatpack-entity-name", ("name", resultPrototype.Name)), metadata);
        _metaData.SetEntityDescription(ent,
            Loc.GetString("flatpack-entity-description", ("name", resultPrototype.Name)), metadata);
        Dirty(ent, metadata);
        return true;
    }
}
