// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Runtime;
using Robust.Shared.Player;

namespace Content.Server._NC.Meta.Runtime;

public sealed partial class NCMetaRuntimeSystem
{
    private readonly Dictionary<EntityUid, HashSet<NCMetaRuntimeInstance>> _attendedServices = [];

    private void InitializeServiceAttendance()
    {
        SubscribeLocalEvent<PlayerDetachedEvent>(OnServicePlayerDetached);
        _serviceAuthority.ManagementAuthorityChanged += OnServiceManagementChanged;
    }
    private void ShutdownServiceAttendance() => _serviceAuthority.ManagementAuthorityChanged -= OnServiceManagementChanged;
    private void TrackServiceAttendance(NCMetaRuntimeInstance instance)
    {
        if (instance.AttendingProof is not { } proof) return;
        if (!_attendedServices.TryGetValue(proof.ActorEntity, out var services)) _attendedServices.Add(proof.ActorEntity, services = []);
        services.Add(instance);
    }
    private void UntrackServiceAttendance(NCMetaRuntimeInstance instance)
    {
        if (instance.AttendingProof is { } proof && _attendedServices.TryGetValue(proof.ActorEntity, out var services) &&
            services.Remove(instance) && services.Count == 0) _attendedServices.Remove(proof.ActorEntity);
    }
    private void OnServicePlayerDetached(PlayerDetachedEvent args)
    {
        if (!_attendedServices.TryGetValue(args.Entity, out var services)) return;
        foreach (var service in services.ToArray()) Stop(service, MetaStopReason.PermissionRevoked);
    }
    private void OnServiceManagementChanged()
    {
        foreach (var service in _attendedServices.Values.SelectMany(services => services).Distinct().ToArray())
            if (service.AttendingProof == null || !_serviceAuthority.CanAttendService(service.AttendingProof))
                Stop(service, MetaStopReason.PermissionRevoked);
    }
    private void OnServiceAuthorityChanged(Guid installationId)
    {
        if (!_serviceInstallations.TryGetValue(installationId, out var processes)) return;
        foreach (var process in processes.ToArray()) Stop(process, MetaStopReason.PermissionRevoked);
    }
}
