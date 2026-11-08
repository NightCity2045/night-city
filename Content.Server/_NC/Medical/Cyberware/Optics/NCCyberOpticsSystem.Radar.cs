// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using System.Numerics;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared._NC.Medical.Cyberware.Optics;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs;
using Content.Shared.Physics;
using Robust.Shared.Physics.Components;

namespace Content.Server._NC.Medical.Cyberware.Optics;

public sealed partial class NCCyberOpticsSystem
{
    private readonly Dictionary<EntityUid, NCOpticContact[]> _externalViews = [];

    private void UpdateExternalRadars()
    {
        _externalViews.Clear();
        var query = EntityQueryEnumerator<NCOpticRadarComponent>();
        while (query.MoveNext(out var uid, out var radar))
        {
            if (HasComp<NCCyberwareComponent>(uid) || radar.User is not { } user || !Exists(user) || radar.ActiveUntil <= _timing.CurTime ||
                !_interaction.InRangeUnobstructed(user, uid, 1.5f) || !_cells.HasCharge(uid, 1)) continue;
            var contacts = Observe(user, (uid, radar), false, 1);
            _externalViews[user] = contacts;
            SendView(user, new NCOpticViewEvent { Viewer = GetNetEntity(user), Map = Transform(user).MapID, Contacts = contacts, Mode = NCOpticMode.Radar });
        }
    }

    private Entity<NCOpticRadarComponent>? FindRadar(EntityUid body)
    {
        foreach (var uid in _cyberware.GetInstalledModuleEntities(body, 32))
            if (TryComp<NCOpticRadarComponent>(uid, out var sensor) && TryComp<NCCyberwareComponent>(uid, out var implant) && implant.Operational && implant.PowerGranted)
                return (uid, sensor);
        var query = EntityQueryEnumerator<NCOpticRadarComponent>();
        while (query.MoveNext(out var uid, out var radar))
            if (radar.User == body && !HasComp<NCCyberwareComponent>(uid) &&
                _interaction.InRangeUnobstructed(body, uid, 1.5f) && _cells.HasCharge(uid, 1)) return (uid, radar);
        return null;
    }

    private void OnRadarActivate(Entity<NCOpticRadarComponent> ent, ref ActivateInWorldEvent args)
    {
        if (args.Handled || HasComp<NCCyberwareComponent>(ent)) return;
        args.Handled = true;
        if (!_blocker.CanInteract(args.User, ent) || !_interaction.InRangeUnobstructed(args.User, ent.Owner, 1.5f)) return;
        ent.Comp.User = args.User;
        if (TryStartRadar(ent, args.User)) _popup.PopupEntity(Loc.GetString("nc-optics-radar-active"), ent, args.User);
    }

    private bool TryStartRadarFor(EntityUid user) => FindRadar(user) is { } radar && TryStartRadar(radar, user);

    private bool TryStartRadar(Entity<NCOpticRadarComponent> radar, EntityUid user)
    {
        if (_timing.CurTime < radar.Comp.ReadyAt || radar.Comp.Range is <= 0 or > 6 ||
            radar.Comp.BurstSeconds is <= 0 or > 5 || radar.Comp.CooldownSeconds is < 10 or > 60) return false;
        if (HasComp<NCCyberwareComponent>(radar))
        {
            if (!_cyberware.TryGetOwningBody(radar, out var body) || body != user || !_cyberware.TryBeginAbilityLoad(radar, 5)) return false;
        }
        else if (!_cells.TryUseCharge(radar.Owner, 5)) return false;
        radar.Comp.User = user;
        radar.Comp.Observations.Clear(); radar.Comp.NextContact = 0; radar.Comp.NextScan = TimeSpan.Zero; radar.Comp.Contacts = [];
        radar.Comp.ActiveUntil = _timing.CurTime + TimeSpan.FromSeconds(radar.Comp.BurstSeconds);
        radar.Comp.ReadyAt = radar.Comp.ActiveUntil + TimeSpan.FromSeconds(radar.Comp.CooldownSeconds);
        return true;
    }

    private NCOpticContact[] Observe(EntityUid body, Entity<NCOpticRadarComponent>? radar, bool thermal, float quality)
    {
        if (radar is { } movedMap && movedMap.Comp.ScanMap != Transform(body).MapID)
        { movedMap.Comp.Observations.Clear(); movedMap.Comp.Contacts = []; movedMap.Comp.NextScan = TimeSpan.Zero; movedMap.Comp.ScanMap = Transform(body).MapID; }
        if (radar is { } cached && cached.Comp.NextScan > _timing.CurTime) return cached.Comp.Contacts;
        var origin = _transform.GetWorldPosition(body);
        var map = Transform(body).MapID;
        var range = radar?.Comp.Range ?? 6;
        _nearby.Clear(); _lookup.GetEntitiesInRange(Transform(body).Coordinates, range, _nearby, LookupFlags.All);
        _shields.Clear();
        if (!thermal) _lookup.GetEntitiesInRange(Transform(body).Coordinates, range, _shields, LookupFlags.All);
        if (_shields.Count > 128) return [];
        var result = new List<NCOpticContact>();
        foreach (var candidate in _nearby.OrderBy(m => m.Owner.Id).Take(64))
        {
            if (candidate.Owner == body || candidate.Comp.CurrentState == MobState.Dead ||
                Transform(candidate.Owner).MapID != map || _containers.IsEntityInContainer(candidate.Owner)) continue;
            var position = _transform.GetWorldPosition(candidate.Owner);
            if (Vector2.DistanceSquared(position, origin) > range * range) continue;
            if (thermal)
            {
                if (!TryComp<Content.Shared.Temperature.Components.TemperatureComponent>(candidate, out var heat) ||
                    !float.IsFinite(heat.Temperature) || heat.Temperature < 295 ||
                    !_interaction.InRangeUnobstructed(body, candidate.Owner, range)) continue;
                result.Add(new(result.Count + 1, position, 0, true));
                continue;
            }
            if (radar is not { } sensor || _shields.Any(shield => Transform(shield.Owner).MapID == map &&
                NCOpticMath.SegmentIntersectsShield(origin, position, _transform.GetWorldPosition(shield.Owner), .75f))) continue;
            var observations = sensor.Comp.Observations;
            if (!observations.TryGetValue(candidate.Owner, out var prior))
            {
                if (observations.Count >= 64) continue;
                // Initial contact requires observed motion, not mere presence behind a wall.
                observations[candidate.Owner] = (position, TimeSpan.MinValue, ++sensor.Comp.NextContact);
                continue;
            }
            var moved = NCOpticMath.DetectMovement(prior.Position, position, 1);
            var movedAt = moved ? _timing.CurTime : prior.MovedAt;
            observations[candidate.Owner] = (position, movedAt, prior.Id);
            if (movedAt == TimeSpan.MinValue || _timing.CurTime - movedAt > TimeSpan.FromSeconds(1.5)) continue;
            var age = (float) (_timing.CurTime - movedAt).TotalSeconds;
            result.Add(new(prior.Id, NCOpticMath.Quantize(position, .5f / MathF.Max(.5f, quality)), age, false));
        }
        var contacts = result.Take(32).ToArray();
        if (radar is { } scanned)
        {
            scanned.Comp.Contacts = contacts;
            scanned.Comp.NextScan = _timing.CurTime + TimeSpan.FromSeconds(1);
        }
        return contacts;
    }
}
