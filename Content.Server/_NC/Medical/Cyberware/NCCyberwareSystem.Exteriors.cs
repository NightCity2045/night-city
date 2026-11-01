// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Visuals;
using Content.Shared._NC.Medical.Persistence;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Medical.Cyberware;

public sealed partial class NCCyberwareSystem
{
    public void PrepareLegacyExteriors(EntityUid body, NCMedicalStateSnapshot snapshot)
    {
        foreach (var module in snapshot.CyberwareModules) Prepare(module);
        foreach (var organ in snapshot.Organs.Values)
            if (organ.Cyberware is { } module) Prepare(module);
        return;

        void Prepare(NCCyberwareStateSnapshot state)
        {
            if (state.Exterior != null || !_prototypes.TryIndex<EntityPrototype>(state.EntityPrototype, out var prototype) ||
                !prototype.TryComp<NCCyberwareExteriorComponent>(out var definition, _componentFactory)) return;
            state.Exterior = new NCCyberwareExteriorSnapshot { Finish = definition.Finish, Ruptured = definition.Ruptured };
            foreach (var uid in CollectCyberware(body))
            {
                if (Comp<NCCyberwareComponent>(uid).StableInstanceId != state.StableInstanceId ||
                    !TryComp<NCCyberwareExteriorComponent>(uid, out var live)) continue;
                state.Exterior = new NCCyberwareExteriorSnapshot
                {
                    Finish = live.Finish,
                    Ruptured = live.Ruptured,
                    SkinColor = live.SkinColor ?? (TryComp<Content.Shared.Body.VisualOrganComponent>(uid, out var visual) ? visual.Profile.SkinColor : Color.White),
                };
                break;
            }
        }
    }
}
