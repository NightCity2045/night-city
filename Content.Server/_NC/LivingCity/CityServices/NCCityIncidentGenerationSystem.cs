// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server._NC.LivingCity.Identity;
using Content.Shared._NC.LivingCity.CityServices;

namespace Content.Server._NC.LivingCity.CityServices;

/// <summary>Converts only normalized authoritative domain events into idempotent incident aggregates.</summary>
public sealed partial class NCCityIncidentGenerationSystem : EntitySystem
{
    [Dependency] private NCCityIncidentRegistrySystem _incidents = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCityIncidentSourceEvent>(OnSource);
    }

    private void OnSource(NCCityIncidentSourceEvent args)
    {
        if (args.WorldEventId == Guid.Empty || !args.LocationZone.IsValid || !args.Initiator.IsValid)
            return;
        var id = NCPersistentIdentity.CityIncident(args.WorldEventId, args.Kind.ToString());
        _ = _incidents.CreateAsync(new NCCreateIncidentRequest(
            NCPersistentIdentity.Command("incident", "generate", id.ToString()), id, args.Kind, args.Severity,
            args.LocationZone, args.Participants, new NCIncidentCause(args.WorldEventId, args.Domain,
                args.EventType, args.ReferenceId), args.MaterializationPolicy, null, args.Initiator,
            NCPersistentIdentity.Correlation("incident", "generate", id.ToString()), args.WorldEventId,
            $"incident-source:{args.Domain}"));
    }
}
