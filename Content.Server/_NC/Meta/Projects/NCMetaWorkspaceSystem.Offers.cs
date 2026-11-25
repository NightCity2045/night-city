// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Hosting;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Meta.UI;

namespace Content.Server._NC.Meta.Projects;

public sealed partial class NCMetaWorkspaceSystem
{
    private const int MaximumOffers = 64;
    private static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(5);
    private sealed record ProgramOffer(Guid Id, string Visitor, string Site, string Name, IReadOnlyDictionary<string, string> Files,
        string[] Scopes, TimeSpan Expires);
    private readonly Dictionary<string, ProgramOffer> _offers = new(StringComparer.Ordinal);
    private readonly Dictionary<EntityUid, MetaRateLimit> _offerRates = [];

    /// <summary>
    /// A site offers one of the programs installed on its own device to the person browsing it. Nothing is installed here:
    /// the browser on the visitor's device is told what is offered and what it asks for, and the visitor decides.
    /// </summary>
    public bool TryOfferProgram(EntityUid site, string visitorDeviceId, string name, string package)
    {
        if (!_devices.TryGetEntry(site, out var siteDevice) || siteDevice.Owner.Length == 0 ||
            !_devices.TryResolve(visitorDeviceId, out var visitor) || visitor.Entity == site ||
            name.Length is < 1 or > 40 || name.Any(character => character is < ' ' or > '~') ||
            package.Length is < 1 or > 64 || package.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))) return false;
        if (_offers.TryGetValue(visitorDeviceId, out var pending) && pending.Expires > _timing.CurTime) return false;
        if (_offers.Count >= MaximumOffers) return false;
        if (!_offerRates.TryGetValue(site, out var rate)) _offerRates[site] = rate = new MetaRateLimit(2, 1);
        if (!rate.TryConsume(Environment.TickCount64)) return false;
        _ = CreateOfferAsync(siteDevice.Id, siteDevice.Owner, visitorDeviceId, name, package);
        return true;
    }

    private async Task CreateOfferAsync(string siteId, string siteOwner, string visitorId, string name, string package)
    {
        try
        {
            var installed = await _persistence.ReadInstallationsAsync(siteId, siteOwner);
            var installation = installed.FirstOrDefault(value => value.SlotKind == "User" && value.PackageIdentity == package);
            if (installation == null) { Log.Debug($"META offer: no program '{package}' installed on {siteId}; found: {string.Join(",", installed.Select(value => value.SlotKind + "/" + value.PackageIdentity))}"); return; }
            var project = await _persistence.ReadProjectAsync(installation.ProjectId, siteOwner);
            if (project == null) { Log.Debug("META offer: project missing"); return; }
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in project.Files)
            {
                var source = await _persistence.ReadSourceAsync(project, file.Id);
                if (source == null) { Log.Debug("META offer: source missing"); return; }
                files[file.Path] = source;
            }
            var artifact = await ReadVerifiedAsync(installation.Active);
            if (artifact == null) { Log.Debug("META offer: artifact missing"); return; }
            var scopes = artifact.Manifest.RequiredScopes.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            await OnMainAsync(() =>
            {
                if (!_devices.TryResolve(visitorId, out var visitor) || _offers.Count >= MaximumOffers ||
                    _offers.TryGetValue(visitorId, out var pending) && pending.Expires > _timing.CurTime) return false;
                var offer = new ProgramOffer(Guid.NewGuid(), visitorId, siteId, name, files, scopes, _timing.CurTime + OfferLifetime);
                // The event is raised by the game, so what the browser shows is what will be installed, whatever a page claims.
                var told = _runtime.TryPublishEvent(visitor.Entity, "meta.citinet.Browser.offerReceived", process =>
                    process.CreateRecord(process.GetTypeId("meta.citinet.ProgramOffer"), [process.CreateString(offer.Id.ToString("D")),
                        process.CreateString(offer.Name), process.CreateString(offer.Site), process.CreateString(string.Join(",", offer.Scopes))]));
                if (told > 0) _offers[visitorId] = offer; else Log.Debug("META offer: the visitor's browser did not take the event");
                return told > 0;
            });
        }
        catch (Exception exception) { Log.Error($"META program offer failed: {exception}"); }
    }

    /// <summary>What a visitor's device has been offered and has not yet answered, for the window that asks them.</summary>
    public (string Id, string Name, string[] Scopes)? GetPendingOffer(string visitorDeviceId) =>
        _offers.TryGetValue(visitorDeviceId, out var offer) && offer.Expires > _timing.CurTime ? (offer.Id.ToString("D"), offer.Name, offer.Scopes) : null;

    /// <summary>The visitor turns an offer down.</summary>
    public bool TryDeclineOffer(EntityUid terminal, EntityUid user, string id) =>
        _devices.TryGetEntry(terminal, out var device) && _devices.CanManage(terminal, user) && _offers.TryGetValue(device.Id, out var offer) &&
        offer.Id.ToString("D") == id && _offers.Remove(device.Id);

    /// <summary>
    /// The visitor accepts: the offered program is copied to their own device and installed the ordinary way, as if they had
    /// done it by hand, and the permissions it asked for (the ones the browser listed) are the ones it gets.
    /// Returns null on success, or why it did not happen.
    /// </summary>
    public async Task<string?> AcceptOfferAsync(EntityUid terminal, EntityUid user, string id)
    {
        var started = await OnMainAsync<(string Owner, ProgramOffer Offer)?>(() =>
        {
            if (!_devices.TryGetEntry(terminal, out var device) || device.Owner.Length == 0 || !_devices.CanManage(terminal, user) ||
                !_offers.TryGetValue(device.Id, out var found) || found.Id.ToString("D") != id || found.Expires <= _timing.CurTime) return null;
            return (device.Owner, found);
        });
        if (started is not { } begun) return "NotFound";
        var (owner, offer) = begun;
        if (await _authority.CaptureManagementAsync(user, owner) == null) return "AccessDenied";
        var context = await OnMainAsync(() => CaptureApplicationContext(terminal, user));
        if (context == null) return "AccessDenied";
        // One attempt per offer, whatever happens.
        if (!await OnMainAsync(() => _offers.Remove(offer.Visitor))) return "NotFound";

        var state = new NCMetaWorkspaceState();
        var device = await _persistence.ReadDeviceAsync(context.DeviceId);
        if (device == null) return "NotFound";
        var previous = await _persistence.ReadProjectAsync(device.ActiveProject, owner);
        var cloneId = NCMetaPersistenceSystem.StableGuid("offer-clone:" + offer.Id);
        context = await FreshClockAsync(context);
        var cloned = await _persistence.CloneProjectAsync(cloneId, device, offer.Name, offer.Files, context.Epoch, context.Time,
            authority: WorkspaceAuthority(context));
        if (!cloned.Success) return "QuotaExceeded";
        var projectId = NCMetaPersistenceSystem.StableGuid("project-clone:" + owner + ":" + cloneId);
        var project = await _persistence.ReadProjectAsync(projectId, owner);
        if (project == null) return "Conflict";
        context = await FreshClockAsync(context);
        await CompileAsync(context, project, new(NCMetaWorkspaceOperation.Compile, NCMetaPersistenceSystem.StableGuid("offer-compile:" + offer.Id)), state);
        project = await _persistence.ReadProjectAsync(projectId, owner);
        if (project == null) return "Conflict";
        context = await FreshClockAsync(context);
        await RuntimeRequestAsync(context, project, new(NCMetaWorkspaceOperation.Install, Guid.NewGuid(),
            projectId: project.Id, deviceId: context.DeviceId), state);
        context = await FreshClockAsync(context);
        if (previous != null && await _persistence.ReadDeviceAsync(context.DeviceId) is { } current && current.ActiveProject != previous.Id)
            await _persistence.ManageProjectAsync(NCMetaWorkspaceOperation.SelectProject, Guid.NewGuid(), current, previous,
                Guid.Empty, 0, "", context.Epoch, context.Time, WorkspaceAuthority(context));
        var installation = await SelectInstallationAsync(context.DeviceId, owner, project.Id, Guid.Empty);
        if (installation == null) return "Rejected";
        var host = await OnMainAsync<EntityUid?>(() => _devices.TryResolve(context.DeviceId, out var found) ? found.Entity : null);
        if (host == null) return "NotFound";
        context = await FreshClockAsync(context);
        await RuntimeRequestAsync(context, project, new(NCMetaWorkspaceOperation.Start, Guid.NewGuid(),
            projectId: project.Id, deviceId: context.DeviceId, installationId: installation.Id), state);
        return null;
    }

    /// <summary>The context of someone pressing a button in a program window on a device they manage, not in the workspace.</summary>
    private Context? CaptureApplicationContext(EntityUid terminal, EntityUid user)
    {
        if (!_ui.IsUiOpen(terminal, NCMetaUiKey.Application, user) || !_devices.TryGetEntry(terminal, out var device) || device.Owner.Length == 0 ||
            !_parties.TryResolve(user, out var actor) || !_persistence.TryCaptureTime(out var epoch, out var time) || !_devices.CanManage(terminal, user)) return null;
        return !_authority.TryGetManagementProof(user, device.Owner, out var proof) ? null :
            new(terminal, user, device.Id, device.Owner, device.DeviceEpoch, device.AuthorityEpoch, epoch, time, actor.ToString(), proof, true);
    }
}
