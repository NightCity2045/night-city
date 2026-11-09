// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared.Nutrition;

namespace Content.Shared._NC.Medical.Cyberware;

/// <summary>
/// Cyberware reuses organic organ prototypes that carry <c>Edible</c>. A mechanical implant is not food, so it
/// is excluded from ingestion here instead of editing every inherited upstream organ prototype.
/// </summary>
public sealed class NCCyberwareIngestionSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCyberwareComponent, IngestibleEvent>(OnIngestible);
    }

    private void OnIngestible(Entity<NCCyberwareComponent> ent, ref IngestibleEvent args)
    {
        args.Cancelled = true;
    }
}
