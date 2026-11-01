// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using System.Numerics;
using Content.Meta.Runtime;
using Content.Server._NC.Meta.Runtime;
using Content.Server.GameTicking;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.Movement.Components;
using Content.Shared.Stealth;
using Content.Shared.Stealth.Components;
using Content.Shared.Storage;
using Content.Shared.SurveillanceCamera;
using Content.Shared.Item;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Map.Components;

namespace Content.Server._NC.Meta.Domains;

public sealed record NCMetaDroneObservation(EntityUid Target, int Kind, double BearingDegrees, double RangeMeters, double Confidence);

public sealed partial class NCMetaDroneOperationsSystem
{
    private sealed record Contact(Guid Runtime, string Device, EntityUid Sensor, EntityUid Target, ulong ControlEpoch,
        ulong DeviceEpoch, ulong AuthorityEpoch, TimeSpan Expires, MetaProcess Process, MetaValue Reference);
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedSurveillanceCameraSystem _cameras = default!;
    [Dependency] private SharedStealthSystem _stealth = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private MetaDataSystem _metadata = default!;
    private readonly HashSet<Entity<TransformComponent>> _candidates = [];
    private readonly HashSet<Entity<PointLightComponent>> _lights = [];
    private readonly Dictionary<ulong, Contact> _contacts = [];
    private ulong _nextContact;
    private uint _workTick;
    private int _rayWork;
    private TimeSpan _nextPrune;
    private bool _workExhausted;

    private bool TakeWork(int amount)
    {
        if (_workTick != _timing.CurTick.Value) { _workTick = _timing.CurTick.Value; _rayWork = 0; }
        if (_rayWork + amount > 256) { _workExhausted = true; return false; }
        _rayWork += amount; return true;
    }
    private bool CanSense(EntityUid uid, out NCMetaDroneSensorComponent sensor)
    {
        if (!TryComp(uid, out sensor!) || !HasComp<NCMetaDroneComponent>(uid) || !_devices.IsPowered(uid) ||
            !_cameras.CanSample(uid) || _containers.IsEntityOrParentInContainer(uid)) return false;
        return float.IsFinite(sensor.Range) && sensor.Range is > 0 and <= 8 && float.IsFinite(sensor.FieldOfView) &&
            sensor.FieldOfView is > 0 and <= 180 && float.IsFinite(sensor.MinimumExposure) && sensor.MinimumExposure is >= 0.01f and <= 1;
    }
    /// <summary>Native, bounded sensor result. Only the adapter converts targets to process-local opaque contacts.</summary>
    public bool TryObserve(EntityUid uid, out List<NCMetaDroneObservation> observations)
    {
        observations = [];
        _workExhausted = false;
        if (!CanSense(uid, out var sensor) || _timing.CurTime < sensor.NextObservation || !TakeWork(1)) return false;
        sensor.NextObservation = _timing.CurTime + TimeSpan.FromMilliseconds(250);
        _candidates.Clear(); _lookup.GetEntitiesInRange(Transform(uid).Coordinates, sensor.Range, _candidates);
        if (_candidates.Count > 128) return false;
        foreach (var candidate in _candidates.OrderBy(candidate => candidate.Owner.ToString(), StringComparer.Ordinal))
        {
            var target = candidate.Owner;
            if (!TryObserveTarget(uid, target, sensor, out var observation)) continue;
            observations.Add(observation);
            if (observations.Count == 16) break;
        }
        if (_workExhausted) { observations.Clear(); return false; }
        return true;
    }
    private bool TryObserveTarget(EntityUid uid, EntityUid target, NCMetaDroneSensorComponent sensor, out NCMetaDroneObservation observation)
    {
        observation = default!;
        if (uid == target || !Exists(target) || _containers.IsEntityOrParentInContainer(target) || MetaData(target).VisibilityMask != 1 ||
            Transform(uid).MapUid == null || Transform(target).MapUid != Transform(uid).MapUid) return false;
        var kind = HasComp<StorageComponent>(target) ? 1 : HasComp<ItemComponent>(target) ? 0 : HasComp<InputMoverComponent>(target) ? 2 : -1;
        if (kind < 0) return false;
        var delta = _transform.GetWorldPosition(Transform(target)) - _transform.GetWorldPosition(Transform(uid));
        var distance = delta.Length();
        if (!float.IsFinite(distance) || distance is <= 0.01f || distance > sensor.Range) return false;
        var bearing = Math.IEEERemainder(Math.Atan2(delta.Y, delta.X) - _transform.GetWorldRotation(Transform(uid)).Theta, Math.Tau);
        if (Math.Abs(bearing) > sensor.FieldOfView * Math.PI / 360 || !TakeWork(1) ||
            !_interaction.InRangeUnobstructed(uid, target, sensor.Range)) return false;
        var visibility = TryComp<StealthComponent>(target, out var stealth) ? _stealth.GetVisibility(target, stealth) : 1;
        if (!float.IsFinite(visibility) || visibility < 0.25f || !TryExposure(target, sensor.Range, out var exposure) || exposure < sensor.MinimumExposure) return false;
        observation = new(target, kind, bearing * 180 / Math.PI, distance, Math.Clamp(visibility * exposure, 0, 1)); return true;
    }
    private bool TryExposure(EntityUid target, float radius, out double exposure)
    {
        exposure = 0;
        if (Transform(target).MapUid is not { } map || !TryComp<MapLightComponent>(map, out var ambient)) return false;
        var color = ambient.AmbientLightColor;
        if (TryComp<LightCycleComponent>(map, out var cycle)) color = SharedLightCycleSystem.GetColor((map, cycle), cycle.OriginalColor,
            (float) (_timing.CurTime + cycle.Offset - _ticker.RoundStartTimeSpan - _metadata.GetPauseTime(map)).TotalSeconds);
        exposure = 0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B;
        _lights.Clear(); _lookup.GetEntitiesInRange(Transform(target).Coordinates, radius, _lights, LookupFlags.All);
        if (_lights.Count > 32 || !TakeWork(_lights.Count)) return false;
        foreach (var light in _lights)
        {
            var lamp = light.Comp;
            if (!lamp.Enabled || lamp.ContainerOccluded || !float.IsFinite(lamp.Energy) || !float.IsFinite(lamp.Radius) || lamp.Radius <= 0 ||
                !Transform(target).Coordinates.TryDistance(EntityManager, Transform(light.Owner).Coordinates, out var distance) || distance > lamp.Radius ||
                !_interaction.InRangeUnobstructed(target, light.Owner, radius)) continue;
            exposure += Math.Max(0, lamp.Energy) * Math.Max(0, 1 - distance / lamp.Radius) * (0.2126 * lamp.Color.R + 0.7152 * lamp.Color.G + 0.0722 * lamp.Color.B);
        }
        if (!double.IsFinite(exposure)) return false;
        exposure = Math.Clamp(exposure, 0, 1); return true;
    }
    private MetaValue Observe(NCMetaDeviceInvocation invocation)
    {
        if (!_runtime.CanContinue(invocation, false)) return Error(invocation, "PermissionRevoked");
        if (!TryCaptureObservationReferences(invocation.Device, invocation.Runtime.Generation, invocation.Runtime.Process, out var values, out var error)) return Error(invocation, error);
        var process = invocation.Runtime.Process;
        return process.CreateResult(ReturnType(invocation), process.CreateArray(process.GetTypeId("array<meta.drone.Contact>"), values));
    }
    /// <summary>Trusted sensor bridge, not command authority. Physical effects must still pass the runtime's fresh invocation guard.</summary>
    public bool TryCaptureObservationReferences(EntityUid uid, Guid runtimeGeneration, MetaProcess process, out MetaValue[] values, out string error)
    {
        values = []; error = "PermissionRevoked";
        if (runtimeGeneration == Guid.Empty || !_devices.TryGetEntry(uid, out var device) || !TryComp<NCMetaDroneComponent>(uid, out var drone)) return false;
        PruneContacts();
        error = "QuotaExceeded";
        if (_contacts.Count > 4080 || _contacts.Values.Count(contact => contact.Runtime == runtimeGeneration) > 112 ||
            _contacts.Values.Count(contact => contact.Sensor == uid) > 112 || _nextContact > ulong.MaxValue - 16) return false;
        error = "RateLimited";
        if (!TryObserve(uid, out var observations)) return false;
        var result = new List<MetaValue>();
        foreach (var observation in observations)
        {
            var reference = process.CreateOpaque(process.GetTypeId("meta.drone.ContactRef"), ++_nextContact);
            _contacts.Add(_nextContact, new(runtimeGeneration, device.Id, uid, observation.Target,
                drone.ControlEpoch, device.DeviceEpoch, device.AuthorityEpoch, _timing.CurTime + TimeSpan.FromSeconds(2), process, reference));
            result.Add(process.CreateRecord(process.GetTypeId("meta.drone.Contact"), [reference,
                process.CreateEnum(process.GetTypeId("meta.drone.ContactKind"), observation.Kind), MetaValue.Float(observation.BearingDegrees),
                MetaValue.Float(observation.RangeMeters), MetaValue.Float(observation.Confidence), _runtime.CreateStamp(process)]));
        }
        values = result.ToArray(); error = ""; return true;
    }
    private bool TryResolveContact(NCMetaDeviceInvocation invocation, MetaValue reference, out EntityUid target) =>
        TryResolveObservation(invocation.Device, invocation.Runtime.Generation, invocation.Runtime.Process, reference, out target);
    /// <summary>Resolves a live, visible, exact-process/device/control-epoch observation. This never grants actuator authority.</summary>
    public bool TryResolveObservation(EntityUid uid, Guid runtimeGeneration, MetaProcess process, MetaValue reference, out EntityUid target)
    {
        target = default;
        if (!process.TryGetOpaqueToken(reference, out var token) || !_contacts.TryGetValue(token, out var contact) ||
            contact.Reference != reference || !ReferenceEquals(contact.Process, process) || contact.Runtime != runtimeGeneration || contact.Sensor != uid ||
            _timing.CurTime >= contact.Expires || !TryComp<NCMetaDroneComponent>(uid, out var drone) || drone.ControlEpoch != contact.ControlEpoch ||
            !_devices.TryGetEntry(uid, out var device) || device.Id != contact.Device || device.DeviceEpoch != contact.DeviceEpoch || device.AuthorityEpoch != contact.AuthorityEpoch ||
            !CanSense(uid, out var sensor) || !TryObserveTarget(uid, contact.Target, sensor, out _)) return false;
        target = contact.Target; return true;
    }
    private void PruneContacts()
    {
        if (_timing.CurTime < _nextPrune) return;
        _nextPrune = _timing.CurTime + TimeSpan.FromMilliseconds(250);
        foreach (var (token, contact) in _contacts.Where(pair => pair.Value.Expires <= _timing.CurTime).ToArray()) ReleaseContact(token, contact);
    }
    private void ReleaseContact(ulong token, Contact contact) { _contacts.Remove(token); contact.Process.TryReleaseOpaque(contact.Reference); }
}
