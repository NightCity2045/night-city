// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Numerics;
using Content.Client.Weapons.Ranged.Systems;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Spawners;

namespace Content.Client._NC.Medical.Humanity;

public sealed partial class NCCyberEpisodeClientSystem
{
    [Dependency] private GunSystem _guns = default!;
    private static readonly ProtoId<Content.Shared.Tag.TagPrototype> HideContextMenuTag = "HideContextMenu";
    private static readonly EntProtoId ShotPrototype = "BulletPistol";
    private static readonly EntProtoId FlashPrototype = "MuzzleFlashEffect";
    private readonly List<LocalFlight> _flights = [];
    private readonly List<EntityUid> _shotEffects = [];
    private sealed record LocalFlight(EntityUid Entity, MapCoordinates Start, Vector2 End, TimeSpan Started,
        float Duration, Angle Angle, EntProtoId? Impact);

    private void ShowNativeShot(EntityUid observer, Vector2 start, Vector2 end)
    {
        var coordinates = new MapCoordinates(start, Transform(observer).MapID);
        var angle = (end - start).ToWorldAngle();
        // A disposable visual anchor receives the ordinary GunSystem flash animation and point light.
        var source = Spawn("clientsideclone", coordinates);
        _tags.AddTag(source, HideContextMenuTag);
        EnsureComp<TimedDespawnComponent>(source).Lifetime = .4f;
        _shotEffects.Add(source);
        _guns.PlayLocalMuzzleFlash(source, FlashPrototype, angle);

        // Copy the real pistol bullet's Sprite definition, including its shader. No Projectile, Physics,
        // Fixtures or damage components are instantiated; this is only the visible flight of the bullet.
        var prototype = _prototypes.Index(ShotPrototype);
        var visuals = new ComponentRegistry { ["Sprite"] = prototype.Components["Sprite"] };
        var bullet = Spawn("clientsideclone", coordinates, visuals, angle);
        _tags.AddTag(bullet, HideContextMenuTag);
        var impact = ((ProjectileComponent) prototype.Components["Projectile"].Component).ImpactEffect;
        _flights.Add(new(bullet, coordinates, end, _timing.CurTime,
            Vector2.Distance(start, end) / SharedGunSystem.ProjectileSpeed, angle, impact));
    }

    private void UpdateShotVisuals()
    {
        _shotEffects.RemoveAll(uid => !Exists(uid));
        for (var i = _flights.Count - 1; i >= 0; i--)
        {
            var flight = _flights[i];
            if (!Exists(flight.Entity)) { _flights.RemoveAt(i); continue; }
            var progress = flight.Duration <= 0f ? 1f :
                (float) (_timing.CurTime - flight.Started).TotalSeconds / flight.Duration;
            if (progress < 1f)
            {
                _transform.SetMapCoordinates(flight.Entity,
                    new MapCoordinates(Vector2.Lerp(flight.Start.Position, flight.End, progress), flight.Start.MapId));
                continue;
            }
            QueueDel(flight.Entity);
            _flights.RemoveAt(i);
            if (flight.Impact is { } effect)
                _shotEffects.Add(Spawn(effect, new MapCoordinates(flight.End, flight.Start.MapId), rotation: flight.Angle));
        }
    }

    private void ClearShotVisuals()
    {
        foreach (var flight in _flights) if (Exists(flight.Entity)) QueueDel(flight.Entity);
        foreach (var effect in _shotEffects) if (Exists(effect)) QueueDel(effect);
        _flights.Clear();
        _shotEffects.Clear();
    }
}
