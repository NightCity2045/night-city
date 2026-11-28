// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server.GameTicking;
using Content.Shared._NC.Medical.Cyberware;
using Content.Shared.Interaction;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.NightVision;
using Content.Shared.Overlays;
using Robust.Server.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Server._NC.Medical.Cyberware.Optics;

/// <summary>Marks cyberoptics that can drive the body's night vision from a measured ambient light level.</summary>
[RegisterComponent]
public sealed partial class NCCyberEyeLowLightComponent : Component
{
    [DataField] public bool SupportsLowLight;
    [DataField] public float SensorRadius = 8;
    [DataField] public float LowLightLoad = 0.4f;
    public bool RequestedLowLight;
    public bool AddedVision;
    public EntityUid? Body;
}

/// <summary>Owns the low-light filter of cybereyes: ambient light sampling and the borrowed night-vision component.</summary>
public sealed partial class NCCyberEyeLowLightSystem : EntitySystem
{
    [Dependency] private NCCyberwareSystem _cyberware = default!;
    [Dependency] private SharedNightVisionSystem _nightVision = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    private readonly Dictionary<EntityUid, (EntityUid Module, NightVisionComponent Vision)> _visionProviders = [];
    private readonly HashSet<Entity<PointLightComponent>> _lights = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCCyberEyeLowLightComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnShutdown(Entity<NCCyberEyeLowLightComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Comp.Body is { } body && Exists(body))
            Apply(ent.Owner, ent.Comp, body, false);
    }

    /// <summary>Normalized ambient light at the body, from the map light plus unobstructed nearby lamps.</summary>
    public bool TryMeasure(EntityUid body, NCCyberEyeLowLightComponent optics, out double normalized)
    {
        normalized = 0;
        if (!float.IsFinite(optics.SensorRadius) || optics.SensorRadius is <= 0 or > 8 || Transform(body).MapUid is not { } map ||
            !TryComp<MapLightComponent>(map, out var ambient))
            return false;
        var color = ambient.AmbientLightColor;
        if (TryComp<LightCycleComponent>(map, out var cycle))
            color = SharedLightCycleSystem.GetColor((map, cycle), cycle.OriginalColor,
                (float) (_timing.CurTime + cycle.Offset - _ticker.RoundStartTimeSpan - _metadata.GetPauseTime(map)).TotalSeconds);
        var light = 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;
        _lights.Clear();
        _lookup.GetEntitiesInRange(Transform(body).Coordinates, optics.SensorRadius, _lights, LookupFlags.All);
        if (_lights.Count > 32)
            return false;
        foreach (var candidate in _lights)
        {
            var lamp = candidate.Comp;
            if (!lamp.Enabled || lamp.ContainerOccluded || !float.IsFinite(lamp.Energy) || !float.IsFinite(lamp.Radius) || lamp.Radius <= 0 ||
                !Transform(body).Coordinates.TryDistance(EntityManager, Transform(candidate.Owner).Coordinates, out var distance) || distance > lamp.Radius ||
                !_interaction.InRangeUnobstructed(body, candidate.Owner, optics.SensorRadius))
                continue;
            light += Math.Max(0, lamp.Energy) * Math.Max(0, 1 - distance / lamp.Radius) * (0.2126 * lamp.Color.R + 0.7152 * lamp.Color.G + 0.0722 * lamp.Color.B);
        }
        if (!double.IsFinite(light))
            return false;
        normalized = Math.Clamp(light, 0, 1);
        return true;
    }

    public bool TrySetFilter(Entity<NCCyberEyeLowLightComponent?> ent, EntityUid body, bool lowLight)
    {
        if (!Resolve(ent, ref ent.Comp, false) || lowLight && !ent.Comp.SupportsLowLight ||
            !_cyberware.TryGetOwningBody(ent.Owner, out var owner) || owner != body)
            return false;
        return Apply(ent.Owner, ent.Comp, body, lowLight);
    }

    private bool Apply(EntityUid module, NCCyberEyeLowLightComponent optics, EntityUid body, bool lowLight)
    {
        if (lowLight)
        {
            var hasVision = TryComp<NightVisionComponent>(body, out var existing);
            if (_visionProviders.TryGetValue(body, out var provider) && (!hasVision || !ReferenceEquals(provider.Vision, existing)))
                _visionProviders.Remove(body);
            if (_visionProviders.TryGetValue(body, out provider) && provider.Module != module)
                return false;
            // Another source already grants night vision; never take it over or remove it later.
            if (!_visionProviders.ContainsKey(body) && hasVision)
                return false;
            var vision = EnsureComp<NightVisionComponent>(body);
            _nightVision.SetEnabled((body, vision), true, body);
            _visionProviders[body] = (module, vision);
            optics.AddedVision = true;
        }
        else if (_visionProviders.TryGetValue(body, out var provider) && provider.Module == module)
        {
            _visionProviders.Remove(body);
            if (TryComp<NightVisionComponent>(body, out var current) && ReferenceEquals(provider.Vision, current))
            {
                _nightVision.SetEnabled((body, current), false, body);
                if (optics.AddedVision)
                    RemComp(body, current);
            }
            optics.AddedVision = false;
        }
        optics.Body = lowLight ? body : null;
        optics.RequestedLowLight = lowLight;
        return true;
    }
}
