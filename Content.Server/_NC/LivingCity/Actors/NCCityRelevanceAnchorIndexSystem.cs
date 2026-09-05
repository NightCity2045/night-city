// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Server._NC.LivingCity.Actors.Components;

namespace Content.Server._NC.LivingCity.Actors;

/// <summary>
/// Single event owner for round-runtime camera/script relevance anchors. Consumers receive copied entity IDs and do
/// their own bounded coordinate policy; the index never scans entities.
/// </summary>
public sealed partial class NCCityRelevanceAnchorIndexSystem : EntitySystem
{
    private readonly HashSet<EntityUid> _anchors = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityRelevanceAnchorComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<NCCityRelevanceAnchorComponent, ComponentShutdown>(OnShutdown);
    }

    public void GetAnchors(List<EntityUid> result)
    {
        result.Clear();
        result.AddRange(_anchors.Order());
    }

    private void OnStartup(Entity<NCCityRelevanceAnchorComponent> entity, ref ComponentStartup args)
    {
        _anchors.Add(entity.Owner);
    }

    private void OnShutdown(Entity<NCCityRelevanceAnchorComponent> entity, ref ComponentShutdown args)
    {
        _anchors.Remove(entity.Owner);
    }
}
