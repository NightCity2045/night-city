// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Content.Meta.Hosting;
using Content.Meta.Mui;
using Content.Server.Database;
using Content.Server._NC.Agent;
using Content.Server._NC.Meta.Devices;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Authority;
using Content.Server._NC.Meta.Commerce;
using Content.Shared._NC.Agent.Components;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.UI;
using Content.Shared.PDA;
using Content.Shared._NC.Persistence.Time;
using Robust.Shared.Asynchronous;
using Robust.Shared.Timing;

namespace Content.Server._NC.Meta.Applications;

public sealed record NCMetaSiteOutcome(bool Success, string Address = "");

/// <summary>Durable, explicit publications on the existing authenticated CitiNet Agent route.</summary>
public sealed partial class NCMetaCitiNetSystem : EntitySystem
{
    private const string PublicationType = "meta.site-publication";
    private sealed record Publication(string HostId, string Owner, string PackageDigest, string View, bool Active, long Revision);
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 8 };
    [Dependency] private NCMetaDeviceRegistrySystem _devices = default!;
    [Dependency] private NCMetaApplicationSystem _applications = default!;
    [Dependency] private NCMetaHostingSystem _hosting = default!;
    [Dependency] private NCMetaPersistenceSystem _persistence = default!;
    [Dependency] private NCMetaAuthoritySystem _authority = default!;
    [Dependency] private NCMetaMarketplaceSystem _marketplace = default!;
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private ITaskManager _tasks = default!;
    [Dependency] private NCAgentSystem _agents = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;
    [Dependency] private IGameTiming _timing = default!;
    private readonly Dictionary<Guid, Publication> _sites = [];
    private readonly Dictionary<EntityUid, (MetaRateLimit Rate, Guid LastRequest, TimeSpan Expires)> _navigation = [];
    private NCSessionEpoch _epoch;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCAgentComponent, NCMetaBrowserNavigateMessage>(OnNavigate);
        SubscribeLocalEvent<NCMetaBrowserUiLifecycleComponent, BoundUIClosedEvent>(OnClosed);
        SubscribeLocalEvent<NCMetaBrowserUiLifecycleComponent, ComponentShutdown>(OnShutdown);
    }

    public async Task<NCMetaSiteOutcome> TryPublishSelfHostedAsync(EntityUid host, EntityUid user, Guid requestId, string view, Guid runtimeGeneration = default)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        if (mutation is null) return new(false);
        if (requestId == Guid.Empty || !_devices.CanManage(host, user) || !_devices.TryGetEntry(host, out var device) ||
            !_authority.TryGetManagementProof(user, device.Owner, out var management) ||
            !Guid.TryParseExact(device.Id, "D", out var id) || !Connected(host) ||
            !(runtimeGeneration == Guid.Empty ? _applications.TryGetRegistration(host, out var app) :
                _applications.TryGetRegistration(host, runtimeGeneration, out app)) || !app.Application.Views.ContainsKey(view) ||
            !_persistence.TryCaptureTime(out var epoch, out var time))
            return new(false);
        var hostId = device.Id;
        var owner = app.Owner;
        var generation = app.RuntimeGeneration;
        var packageDigest = app.Application.PackageDigest;
        if (management.Owner.ToString() != owner) return new(false);
        var current = await ReadPublicationAsync(id);
        var next = new Publication(hostId, owner, packageDigest, view, true, (current?.Revision ?? 0) + 1);
        var authority = await Main(() => _authority.IsCurrent(management) && _devices.CanManage(host, user) &&
            _devices.TryGetEntry(host, out var deviceNow) && deviceNow.Id == hostId && deviceNow.Owner == owner &&
            deviceNow.DeviceEpoch == device.DeviceEpoch && deviceNow.AuthorityEpoch == device.AuthorityEpoch &&
            _applications.TryGetRegistration(host, generation, out var fresh) &&
            fresh.Owner == owner && fresh.RuntimeGeneration == generation && Connected(host) &&
            _persistence.TryCaptureTime(out var currentEpoch, out _) && currentEpoch == epoch
                ? _authority.CaptureWriteConstraints(management, host) : null);
        if (authority == null)
            return new(false);
        var persisted = await _database.ApplyNCPersistentMutationAsync(new(requestId, "meta.site.publish", "meta.owner", owner,
            Guid.NewGuid(), "meta.site.published", PublicationType, hostId, current?.Revision ?? 0, 1,
            JsonSerializer.Serialize(next, Json), "{}", "{}", requestId, null, epoch, time, [],
            RequestFingerprint: Fingerprint(owner, hostId, packageDigest, view), Authority: authority));
        if (persisted.Status is not (NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted))
            return new(false);
        var committed = await ReadPublicationAsync(id);
        var enabled = committed is not null && committed.Active && committed.Owner == owner && committed.PackageDigest == packageDigest &&
            committed.View == view && await Main(() => Restore(id, committed));
        return new(enabled, enabled ? new MuiCitiNetAddress(id, view).ToString() : "");
    }

    public async Task<bool> TryWithdrawAsync(EntityUid host, EntityUid user, Guid requestId)
    {
        using var mutation = _persistence.TryBeginNativeMutation();
        if (mutation is null) return false;
        if (requestId == Guid.Empty || !_devices.CanManage(host, user) || !_devices.TryGetEntry(host, out var device) ||
            !_authority.TryGetManagementProof(user, device.Owner, out var management) ||
            !Guid.TryParseExact(device.Id, "D", out var id) || !_persistence.TryCaptureTime(out var epoch, out var time))
            return false;
        var owner = device.Owner;
        var current = await ReadPublicationAsync(id);
        if (current is null || current.Owner != owner)
            return false;
        var authority = await Main(() => _authority.IsCurrent(management) && _devices.CanManage(host, user) &&
            _devices.TryGetEntry(host, out var fresh) && fresh.Id == device.Id && fresh.Owner == owner &&
            fresh.DeviceEpoch == device.DeviceEpoch && fresh.AuthorityEpoch == device.AuthorityEpoch &&
            _persistence.TryCaptureTime(out var currentEpoch, out _) && currentEpoch == epoch
                ? _authority.CaptureWriteConstraints(management, host) : null);
        if (authority == null)
            return false;
        var next = current with { Active = false, Revision = current.Revision + 1 };
        var result = await _database.ApplyNCPersistentMutationAsync(new(requestId, "meta.site.withdraw", "meta.owner", owner,
            Guid.NewGuid(), "meta.site.withdrawn", PublicationType, device.Id, current.Revision, 1,
            JsonSerializer.Serialize(next, Json), "{}", "{}", requestId, null, epoch, time, [],
            RequestFingerprint: Fingerprint(owner, device.Id, "withdraw"), Authority: authority));
        if (result.Status is not (NCPersistentMutationStatus.Applied or NCPersistentMutationStatus.DuplicateCompleted))
            return false;
        var committed = await ReadPublicationAsync(id);
        if (committed is null) return false;
        await Main(() => { Cache(id, committed); return true; });
        return !committed.Active;
    }

    private bool Connected(EntityUid host) => _marketplace.HasCitiNetRoute(host);
    private bool Restore(Guid id, Publication publication)
    {
        Cache(id, publication);
        return publication.Active && _devices.TryResolve(publication.HostId, out var device) && device.Owner == publication.Owner && Connected(device.Entity) &&
            _applications.TryRestorePublishedView(device.Entity, publication.Owner, publication.PackageDigest, publication.View);
    }
    private void Cache(Guid id, Publication publication)
    {
        if (_sites.TryGetValue(id, out var prior) && prior.Revision > publication.Revision || _sites.Count >= 1024 && !_sites.ContainsKey(id))
            return;
        _sites[id] = publication;
    }
    private bool IsCurrent(Guid id, Publication publication) => _persistence.TryCaptureTime(out var epoch, out _) && epoch == _epoch &&
        _sites.TryGetValue(id, out var current) && current == publication && publication.Active &&
        _devices.TryResolve(publication.HostId, out var device) && device.Owner == publication.Owner && Connected(device.Entity) &&
        _applications.TryGetRegistration(device.Entity, publication.PackageDigest, out var app) && app.Owner == publication.Owner &&
        app.Application.PackageDigest == publication.PackageDigest && app.Policy.PublicViews.Contains(publication.View);

    private async Task<Publication?> ReadPublicationAsync(Guid id)
    {
        var snapshot = await _persistence.ReadSnapshotAsync(PublicationType, id.ToString("D"));
        if (snapshot is null || snapshot.FormatVersion != 1 || snapshot.Payload.Length > 4096)
            return null;
        try
        {
            var value = JsonSerializer.Deserialize<Publication>(snapshot.Payload, Json);
            return value is not null && value.HostId == id.ToString("D") && value.Revision == snapshot.AggregateVersion &&
                value.Owner.Length is > 0 and <= 128 && value.PackageDigest.Length == 64 && MuiBinding.IsPath(value.View) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private void OnNavigate(Entity<NCAgentComponent> ent, ref NCMetaBrowserNavigateMessage args)
    {
        if (!EnsureEpoch() || args.RequestId == Guid.Empty || args.Address.Length > 192 || !_agents.CanUseMetaCitiNet(ent.Owner, args.Actor))
            return;
        EnsureComp<NCMetaBrowserUiLifecycleComponent>(ent.Owner);
        var now = _timing.CurTime;
        if (!_navigation.TryGetValue(ent.Owner, out var navigation) || navigation.Expires < now)
        {
            if (_navigation.Count >= 1024 && !_navigation.ContainsKey(ent.Owner))
                return;
            navigation = (new MetaRateLimit(4, 2), Guid.Empty, now + TimeSpan.FromMinutes(5));
        }
        if (navigation.LastRequest == args.RequestId || !navigation.Rate.TryConsume((long) now.TotalMilliseconds))
            return;
        _navigation[ent.Owner] = (navigation.Rate, args.RequestId, now + TimeSpan.FromMinutes(5));
        if (!MuiCitiNetAddress.TryParse(args.Address, out var address))
        { Unavailable(ent.Owner, args.Actor); return; }
        _ = NavigateAsync(ent.Owner, args.Actor, args.RequestId, address!, _epoch);
    }

    private async Task NavigateAsync(EntityUid agent, EntityUid user, Guid requestId, MuiCitiNetAddress address, NCSessionEpoch expectedEpoch)
    {
        try
        {
            var lease = address.Domain is null ? null : await _hosting.ResolveDomainAsync(address.Domain);
            var publication = address.Domain is null ? await ReadPublicationAsync(address.HostDeviceId) : null;
            await Main(() =>
            {
                if (!EnsureEpoch() || _epoch != expectedEpoch || !_navigation.TryGetValue(agent, out var navigation) ||
                    navigation.LastRequest != requestId || !_agents.CanUseMetaCitiNet(agent, user))
                    return false;
                bool opened;
                if (lease is not null && (address.View is null || address.View == lease.View) && _hosting.CanUseLease(lease.Id, lease.Revision) &&
                    _devices.TryResolve(lease.HostDeviceId, out var target) && Connected(target.Entity) &&
                    _applications.TryRestorePublishedView(target.Entity, lease.Provider, lease.PackageDigest, lease.View) &&
                    _applications.TryGetRegistration(target.Entity, lease.PackageDigest, out var leasedApplication))
                    opened = _applications.TryOpen(agent, user, target.Entity, lease.View, NCMetaAppPresentation.Website, PdaUiKey.Key,
                        () => _agents.CanUseMetaCitiNet(agent, user) && Connected(target.Entity) && _hosting.CanUseLease(lease.Id, lease.Revision),
                        out _, lease.Units > 0 ? lease.Id : Guid.Empty, lease.MaxSessions, leasedApplication.RuntimeGeneration);
                else if (publication is not null && (address.View is null || address.View == publication.View) && Restore(address.HostDeviceId, publication) &&
                    IsCurrent(address.HostDeviceId, publication) && _devices.TryResolve(publication.HostId, out var device) &&
                    _applications.TryGetRegistration(device.Entity, publication.PackageDigest, out var publishedApplication))
                    opened = _applications.TryOpen(agent, user, device.Entity, publication.View, NCMetaAppPresentation.Website, PdaUiKey.Key,
                        () => _agents.CanUseMetaCitiNet(agent, user) && IsCurrent(address.HostDeviceId, publication), out _,
                        runtimeGeneration: publishedApplication.RuntimeGeneration);
                else
                    opened = false;
                if (!opened)
                    Unavailable(agent, user);
                return opened;
            });
        }
        catch (Exception exception)
        {
            Log.Error($"META CitiNet navigation failed: {exception}");
            await Main(() => { if (_agents.CanUseMetaCitiNet(agent, user)) Unavailable(agent, user); return false; });
        }
    }

    private void Unavailable(EntityUid agent, EntityUid user) =>
        _ui.ServerSendUiMessage(agent, PdaUiKey.Key, new NCMetaBrowserResultMessage(Loc.GetString("nc-meta-site-unavailable")), user);
    private Task<T> Main<T>(Func<T> action)
    {
        var result = new TaskCompletionSource<T>();
        _tasks.RunOnMainThread(() => { try { result.SetResult(action()); } catch (Exception exception) { result.SetException(exception); } });
        return result.Task;
    }
    private static string Fingerprint(params string[] parts) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(parts))));
    private bool EnsureEpoch()
    {
        if (!_persistence.TryCaptureTime(out var epoch, out _)) return false;
        if (_epoch == epoch) return true;
        _epoch = epoch; _sites.Clear(); _navigation.Clear();
        return true;
    }
    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        EnsureEpoch();
    }
    private void OnClosed(Entity<NCMetaBrowserUiLifecycleComponent> ent, ref BoundUIClosedEvent args) => _navigation.Remove(ent.Owner);
    private void OnShutdown(Entity<NCMetaBrowserUiLifecycleComponent> ent, ref ComponentShutdown args) => _navigation.Remove(ent.Owner);
    public override void Shutdown()
    {
        _sites.Clear(); _navigation.Clear();
        base.Shutdown();
    }
}

