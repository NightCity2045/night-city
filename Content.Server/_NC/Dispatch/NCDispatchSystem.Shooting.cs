// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Dispatch;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;

namespace Content.Server._NC.Dispatch;

/// <summary>
/// NCPD gunshot sensors: gunfire in a district under police coverage raises a shooting incident. Shots close to an
/// open shooting add to it instead of raising another. Police officers' own shots are not reported.
/// </summary>
public sealed partial class NCDispatchSystem
{
    /// <summary>Shots within this range of an active shooting incident belong to it.</summary>
    public const float ShootingMergeRange = 30f;

    /// <summary>Shots after this quiet time start a new incident even close to an old one.</summary>
    public static readonly TimeSpan ShootingMergeTime = TimeSpan.FromMinutes(3);

    private void InitializeShooting()
    {
        SubscribeLocalEvent<GunComponent, GunShotEvent>(OnGunShot);
    }

    private void OnGunShot(Entity<GunComponent> ent, ref GunShotEvent args)
    {
        ReportGunfire(args.User);
    }

    /// <summary>Reports a shot fired by the shooter, if the sensors there are watched.</summary>
    public void ReportGunfire(EntityUid shooter)
    {
        if (IsMember(shooter, PoliceService) ||
            !_mapCoordinates.TryGetCoordinates(shooter, out var location) ||
            !_coverage.IsCovered(location))
        {
            return;
        }

        var now = _timing.CurTime;
        foreach (var incident in _incidents)
        {
            if (incident.Kind != NCDispatchIncidentKind.Shooting ||
                incident.Location.NetworkId != location.NetworkId ||
                incident.Location.Z != location.Z ||
                now - incident.LastShot > ShootingMergeTime ||
                (incident.Location.Position - location.Position).Length() > ShootingMergeRange)
            {
                continue;
            }

            incident.Shots++;
            incident.LastShot = now;
            return;
        }

        var created = CreateIncident(PoliceService, NCDispatchIncidentKind.Shooting, shooter, location,
            Loc.GetString("nc-dispatch-caller-sensor"), Loc.GetString("nc-dispatch-shooting-message"));
        created.Shots = 1;
        Announce(created);
    }
}
