// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using System.Collections.Immutable;
using System.Threading;
using Content.Meta.Bytecode;
using Content.Meta.Language;
using Content.Meta.Verification;
using Content.Meta.Mui;
using Content.Server._NC.Meta.Applications;
using Content.Server._NC.Meta.Persistence;
using Content.Server._NC.Meta.Runtime;
using Content.Shared._NC.Meta.UI;

namespace Content.Server._NC.Meta.Projects;

public sealed partial class NCMetaWorkspaceSystem
{
    [Dependency] private NCMetaRuntimeSystem _runtime = default!;
    [Dependency] private NCMetaApplicationSystem _applications = default!;
    [Dependency] private NCMetaCitiNetSystem _citiNet = default!;
    [Dependency] private NCMetaHostingSystem _hosting = default!;
    [Dependency] private NCMetaHardwareSystem _hardware = default!;
    [Dependency] private Robust.Shared.Prototypes.IPrototypeManager _hardwareProfiles = default!;
    private readonly Dictionary<(EntityUid, EntityUid), (EntityUid Host, Guid Generation)> _debug = [];
    private readonly object _artifactGate = new();
    private readonly Dictionary<string, VerifiedArtifact> _verified = new(StringComparer.Ordinal);
    private readonly Queue<(string Digest, long Weight)> _verifiedOrder = [];
    private readonly SemaphoreSlim _verificationWorkers = new(2, 2);
    private long _verifiedWeight;

    public async Task<bool> TryRestoreInstallationAsync(EntityUid host, string id, string owner)
    {
        var epoch = await OnMainAsync(() => _persistence.TryCaptureTime(out var world, out _) ? world : default);
        var installations = await _persistence.ReadInstallationsAsync(id, owner);
        var success = true;
        foreach (var installation in installations.Where(value => value.AutoStart).OrderBy(value => value.SlotKind).ThenBy(value => value.Id))
        {
            var authorization = await _marketplace.ResolveRunAuthorizationAsync(owner, id, installation.Active);
            if (authorization == null) { success = false; continue; }
            var artifact = await ReadVerifiedAsync(installation.Active);
            if (artifact == null) { success = false; continue; }
            var app = await ReadApplicationAsync(installation.Active, artifact);
            if (app.Declared && app.Application == null) { success = false; continue; }
            var serviceAdmission = await _authority.ResolveServiceAdmissionAsync(installation, artifact, unattended: true);
            if (serviceAdmission == null) { success = false; continue; }
            success &= await OnMainAsync(() =>
            {
                if (!_persistence.TryCaptureTime(out var current, out _) || current != epoch || !_hardware.CanAutoStart(host) ||
                    !_devices.TryGetEntry(host, out var device) || device.Id != id || device.Owner != owner) return false;
                if (_runtime.TryGetInstallation(host, owner, installation.Id, out _)) return true;
                if (!_runtime.TryStartRecovered(host, installation, artifact, app.Application != null, authorization.ExpiresAtTicks, authorization.Contact, serviceAdmission) ||
                    !_runtime.TryGetInstallation(host, owner, installation.Id, out var runtime)) return false;
                return app.Application == null || _applications.TryRegisterRecovered(host, owner, app.Application,
                    NCMetaAppPolicy.OwnerOnly with { GrantedScopes = serviceAdmission.GetGrantedScopes().ToImmutableHashSet(StringComparer.Ordinal) }, runtime.Generation);
            });
        }
        return success;
    }

    private (int StorageBytes, int Slots)? InstallationLimits(EntityUid host) =>
        TryComp<Content.Shared._NC.Meta.NCMetaHostComponent>(host, out var hardware) && _hardwareProfiles.TryIndex(hardware.Profile, out var profile)
            && profile.AllowUserPackages ? (TryComp<Content.Shared._NC.Meta.NCMetaStorageComponent>(host, out var storage)
                ? Math.Min(profile.StorageBytes, storage.CapacityBytes) : profile.StorageBytes, 64) : null;

    private async Task<NCMetaInstallation?> SelectInstallationAsync(string deviceId, string owner, Guid projectId, Guid releaseId,
        Guid installationId = default)
    {
        var installations = await _persistence.ReadInstallationsAsync(deviceId, owner);
        if (installationId != Guid.Empty) return installations.FirstOrDefault(value => value.Id == installationId);
        if (releaseId != Guid.Empty) return installations.FirstOrDefault(value => value.Active.Id == releaseId);
        var project = installations.Where(value => value.ProjectId == projectId).ToArray();
        return project.Length == 1 ? project[0] : installations.Length == 1 ? installations[0] : null;
    }

    private async Task<VerifiedArtifact?> ReadVerifiedAsync(NCMetaRelease release)
    {
        lock (_artifactGate) if (_verified.TryGetValue(release.Digest, out var cached)) return cached;
        var bytes = await _persistence.ReadArtifactAsync(release.Digest);
        if (bytes == null) return null;
        if (!await _verificationWorkers.WaitAsync(TimeSpan.FromSeconds(1))) return null;
        try
        {
            var verified = await Task.Run(() =>
            {
                var module = MetaBytecodeLoader.Load(bytes);
                return MetaVerifier.Verify(bytes, MetaApiCatalog.Default.CreateVerificationCatalog(module));
            });
            var weight = verified.ResidentMemoryBytes;
            lock (_artifactGate)
            {
                if (!_verified.ContainsKey(release.Digest) && weight <= 32 * 1024 * 1024)
                {
                    while ((_verifiedWeight + weight > 32 * 1024 * 1024 || _verified.Count >= 32) && _verifiedOrder.TryDequeue(out var old))
                        if (_verified.Remove(old.Digest)) _verifiedWeight -= old.Weight;
                    _verified.Add(release.Digest, verified);
                    _verifiedOrder.Enqueue((release.Digest, weight));
                    _verifiedWeight += weight;
                }
            }
            return verified;
        }
        catch (MetaFormatException) { return null; }
        finally { _verificationWorkers.Release(); }
    }

    private async Task PopulateReleaseAsync(NCMetaRelease release, NCMetaWorkspaceState state)
    {
        var artifact = await ReadVerifiedAsync(release);
        if (artifact == null) return;
        state.RequestedScopes = string.Join(", ", artifact.Manifest.RequiredScopes);
        var app = await ReadApplicationAsync(release, artifact);
        if (app.Application != null) state.AppContractJson = app.Application.ContractJson;
        state.Links = artifact.GetLinks().Select(link => new NCMetaLinkSummary(link.SlotName,
            artifact.GetType(link.CapabilityTypeId).Name, link.ContractHash,
            MetaApiCatalog.Default.Members.Where(member => member.Owner == artifact.GetType(link.CapabilityTypeId).Name &&
                artifact.Manifest.RequiredScopes.Contains(member.Scope)).Select(member => member.Scope).Distinct().ToList())).ToList();
    }

    private async Task RuntimeRequestAsync(Context context, NCMetaProject project, NCMetaWorkspaceRequestMessage request,
        NCMetaWorkspaceState state)
    {
        var target = request.DeviceId.Length == 0 ? context.DeviceId : request.DeviceId;
        if (request.Operation is NCMetaWorkspaceOperation.EnableHostingOffer or NCMetaWorkspaceOperation.QuoteHosting or
            NCMetaWorkspaceOperation.PurchaseHosting or NCMetaWorkspaceOperation.RefundHosting)
        {
            var hostingInstallation = await SelectInstallationAsync(target, context.Owner, project.Id, request.ReleaseId, request.InstallationId);
            var hostingGeneration = await OnMainAsync(() => hostingInstallation != null && _devices.TryResolve(target, out var providerDevice) &&
                _runtime.TryGetInstallation(providerDevice.Entity, context.Owner, hostingInstallation.Id, out var hostedRuntime)
                    ? hostedRuntime.Generation : Guid.Empty);
            NCMetaHostingOutcome hostingOutcome;
            if (request.Operation == NCMetaWorkspaceOperation.QuoteHosting)
            {
                try
                {
                    using var json = JsonDocument.Parse(request.Source, new JsonDocumentOptions { MaxDepth = 4 });
                    var root = json.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(property => property.Name is not ("Domain" or "View" or "Units")))
                        throw new JsonException();
                    var domain = root.GetProperty("Domain").GetString() ?? "";
                    var view = root.GetProperty("View").GetString() ?? "";
                    var units = root.GetProperty("Units").GetInt32();
                    hostingOutcome = await await OnMainAsync(() => _hosting.TryQuoteAsync(context.Terminal, context.User,
                        request.RequestId, domain, target, view, units, hostingGeneration));
                }
                catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
                { state.Notice = Loc.GetString("nc-meta-error-input"); return; }
            }
            else if (request.Operation == NCMetaWorkspaceOperation.EnableHostingOffer)
            {
                var provider = await OnMainAsync<EntityUid?>(() => _devices.TryResolve(target, out var device) ? device.Entity : null);
                if (provider == null) { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
                hostingOutcome = await await OnMainAsync(() => _hosting.TryOfferAsync(provider.Value, context.User,
                    request.RequestId, request.Source, hostingGeneration));
            }
            else
            {
                if (!Guid.TryParseExact(request.Source, "D", out var id)) { state.Notice = Loc.GetString("nc-meta-error-input"); return; }
                hostingOutcome = request.Operation == NCMetaWorkspaceOperation.PurchaseHosting
                    ? await await OnMainAsync(() => _hosting.TryPurchaseAsync(context.Terminal, context.User, request.RequestId, id))
                    : await await OnMainAsync(() => _hosting.TryRefundAsync(context.Terminal, context.User, request.RequestId, id));
            }
            state.Notice = Loc.GetString(hostingOutcome.MessageKey);
            state.HostingQuote = hostingOutcome.Quote;
            state.HostingLeases = (await _hosting.ReadOwnerLeasesAsync(context.Owner)).ToList();
            return;
        }
        var host = await OnMainAsync<EntityUid?>(() => IsCurrent(context) && _devices.TryResolve(target, out var device) &&
            _devices.CanManage(device.Entity, context.User, requireRange: false) && _runtime.CanReach(context.Terminal, device.Entity)
                ? device.Entity : null);
        if (host == null) { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
        if (request.Operation is NCMetaWorkspaceOperation.OpenApplication or NCMetaWorkspaceOperation.PublishWebsite or NCMetaWorkspaceOperation.WithdrawWebsite)
        {
            if (request.Operation == NCMetaWorkspaceOperation.PublishWebsite)
            {
                var publicationInstallation = await SelectInstallationAsync(target, context.Owner, project.Id, request.ReleaseId, request.InstallationId);
                var publicationGeneration = await OnMainAsync(() => publicationInstallation != null &&
                    _runtime.TryGetInstallation(host.Value, context.Owner, publicationInstallation.Id, out var publicationRuntime)
                        ? publicationRuntime.Generation : Guid.Empty);
                var published = await await OnMainAsync(() => _citiNet.TryPublishSelfHostedAsync(host.Value, context.User,
                    request.RequestId, request.Source, publicationGeneration));
                state.WebsiteAddress = published.Address;
                if (!published.Success) state.Notice = Loc.GetString("nc-meta-error-access");
                return;
            }
            if (request.Operation == NCMetaWorkspaceOperation.WithdrawWebsite)
            {
                if (!await await OnMainAsync(() => _citiNet.TryWithdrawAsync(host.Value, context.User, request.RequestId)))
                    state.Notice = Loc.GetString("nc-meta-error-access");
                return;
            }
            var openedInstallation = await SelectInstallationAsync(target, context.Owner, project.Id, request.ReleaseId, request.InstallationId);
            await OnMainAsync(() =>
            {
                if (!IsCurrent(context) || openedInstallation == null ||
                    !_runtime.TryGetInstallation(host.Value, context.Owner, openedInstallation.Id, out var openedRuntime)) return false;
                var opened = _applications.TryOpenDefault(context.Terminal, context.User, host.Value, out var notice,
                    request.Source.Length == 0 ? null : request.Source, openedRuntime.Generation);
                state.Notice = notice;
                return opened;
            });
            return;
        }
        if (request.Operation is >= NCMetaWorkspaceOperation.DebugAttach and <= NCMetaWorkspaceOperation.DebugEvaluate)
        {
            var debugged = await SelectInstallationAsync(target, context.Owner, project.Id, request.ReleaseId, request.InstallationId);
            if (debugged != null) await OnMainAsync(() => HandleDebug(context, host.Value, debugged.Id, request, state));
            return;
        }
        if (request.Operation == NCMetaWorkspaceOperation.Stop)
        {
            var stoppedInstallation = await SelectInstallationAsync(target, context.Owner, project.Id, request.ReleaseId, request.InstallationId);
            await OnMainAsync(() =>
            {
                CancelInstallationRecovery(host.Value, stoppedInstallation?.Id ?? request.InstallationId);
                return true;
            });
            if (stoppedInstallation is { AutoStart: true })
            {
                var stopAuthority = await CaptureInstallationAuthorityAsync(context, host.Value);
                if (stopAuthority == null || !await _persistence.SetAutoStartAsync(request.RequestId,
                        stoppedInstallation, false, context.Epoch, context.Time, stopAuthority))
                { state.Notice = Loc.GetString("nc-meta-error-revision"); return; }
            }
            state.Notice = Loc.GetString(stoppedInstallation != null && await OnMainAsync(() => _runtime.TryStop(host.Value, context.User, stoppedInstallation.Id))
                ? "nc-meta-stopped" : "nc-meta-error-action");
            return;
        }
        if (request.Operation == NCMetaWorkspaceOperation.Logs)
        {
            var logged = await SelectInstallationAsync(target, context.Owner, project.Id, request.ReleaseId, request.InstallationId);
            state.Logs = logged == null ? [] : await OnMainAsync(() => _runtime.GetLogs(host.Value, context.User, logged.Id).ToList());
            return;
        }
        var installation = await SelectInstallationAsync(target, context.Owner, project.Id, request.ReleaseId, request.InstallationId);
        if (installation != null && request.Operation is NCMetaWorkspaceOperation.Start or NCMetaWorkspaceOperation.Rollback)
        {
            var requestedInstallationId = installation.Id;
            await OnMainAsync(() => { CancelInstallationRecovery(host.Value, requestedInstallationId); return true; });
        }
        var release = request.Operation == NCMetaWorkspaceOperation.Start ? installation?.Active :
            request.Operation == NCMetaWorkspaceOperation.Rollback ? installation?.Recovery :
            project.Releases.FirstOrDefault(value => value.Id == request.ReleaseId) ?? project.Releases.LastOrDefault();
        if (release == null) { state.Notice = Loc.GetString("nc-meta-error-action"); return; }
        var artifact = await ReadVerifiedAsync(release);
        if (artifact == null) { state.Notice = Loc.GetString("nc-meta-error-artifact"); return; }
        if (request.Operation == NCMetaWorkspaceOperation.Start)
        {
            var authorization = await _marketplace.ResolveRunAuthorizationAsync(context.Owner, target, release,
                online: true, accessDeviceId: context.DeviceId);
            if (authorization == null)
            { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
            var app = await ReadApplicationAsync(release, artifact);
            if (app.Declared && app.Application == null) { state.Notice = Loc.GetString("nc-meta-error-artifact"); return; }
            var serviceAdmission = installation == null ? null : await _authority.ResolveServiceAdmissionAsync(installation, artifact, unattended: false);
            if (serviceAdmission == null) { state.Notice = Loc.GetString("nc-meta-error-grant-required"); return; }
            var writeAuthority = await CaptureInstallationAuthorityAsync(context, host.Value);
            if (writeAuthority == null) { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
            if (installation is { AutoStart: false } && serviceAdmission.AllowUnattended)
            {
                if (!await OnMainAsync(() => IsCurrent(context) && _runtime.CanRunInstallation(host.Value, context.User, installation, artifact, installation.Bindings)) ||
                    !await _persistence.SetAutoStartAsync(request.RequestId, installation, true, context.Epoch, context.Time, writeAuthority))
                { state.Notice = Loc.GetString("nc-meta-error-revision"); return; }
                installation = installation with { AutoStart = true, Revision = checked(installation.Revision + 1) };
            }
            state.Notice = Loc.GetString(await OnMainAsync(() =>
            {
                if (!IsCurrent(context) || installation == null ||
                    !_runtime.TryStart(host.Value, context.User, installation, artifact, retainForInvocations: app.Application != null,
                        entitlementExpiresAtTicks: authorization.ExpiresAtTicks, marketplaceContact: authorization.Contact,
                        serviceAdmission: serviceAdmission) ||
                    !_runtime.TryGetInstallation(host.Value, context.Owner, installation.Id, out var runtime)) return false;
                return app.Application == null || _applications.TryRegister(host.Value, context.User, app.Application,
                    NCMetaAppPolicy.OwnerOnly with { GrantedScopes = serviceAdmission.GetGrantedScopes().ToImmutableHashSet(StringComparer.Ordinal) }, runtime.Generation);
            }) ? "nc-meta-started" : "nc-meta-error-access");
            return;
        }
        NCMetaBinding[] bindings;
        try
        {
            bindings = request.Operation == NCMetaWorkspaceOperation.Rollback ? installation!.RecoveryBindings ?? [] :
                request.Source.Length > 0 ? JsonSerializer.Deserialize<NCMetaBinding[]>(request.Source,
                    new JsonSerializerOptions { MaxDepth = 8, PropertyNameCaseInsensitive = true }) ?? [] :
                artifact.GetLinks().Select(link => new NCMetaBinding(link.SlotName, target, link.ContractHash,
                    MetaApiCatalog.Default.Members.Where(member => member.Owner == artifact.GetType(link.CapabilityTypeId).Name &&
                        artifact.Manifest.RequiredScopes.Contains(member.Scope)).Select(member => member.Scope).Distinct().ToArray())).ToArray();
        }
        catch (JsonException) { state.Notice = Loc.GetString("nc-meta-error-input"); return; }
        if (bindings.Any(binding => binding == null || binding.Scopes == null) ||
            !await OnMainAsync(() => IsCurrent(context) && (request.Operation == NCMetaWorkspaceOperation.Rollback
                ? installation != null && _runtime.CanRunInstallation(host.Value, context.User, installation, artifact, bindings)
                : _runtime.CanInstall(host.Value, context.User, artifact, bindings))))
        { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
        if (!await CanRunReleaseAsync(context.Owner, target, release))
        { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
        if (request.Operation == NCMetaWorkspaceOperation.Rollback)
        {
            var rollbackAuthority = await CaptureInstallationAuthorityAsync(context, host.Value);
            if (rollbackAuthority == null) { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
            var rolledBack = await _persistence.RollbackInstallationAsync(request.RequestId, installation!, context.Epoch, context.Time, rollbackAuthority);
            if (rolledBack.Success) await OnMainAsync(() => _runtime.TryStop(host.Value, context.User, installation!.Id));
            state.Notice = Loc.GetString(rolledBack.MessageKey);
            return;
        }
        if (release.CommercialOrigin is { } origin)
        {
            var licensed = await InstallLicensedReleaseAsync(context, host.Value, target, request.RequestId,
                origin.ProductId, origin.ReleaseId, bindings, scopeConsent: false);
            state.Notice = Loc.GetString(licensed.MessageKey);
            return;
        }
        var limits = await OnMainAsync(() => IsCurrent(context) ? InstallationLimits(host.Value) : null);
        if (limits == null) { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
        var installedBefore = await _persistence.ReadInstallationsAsync(target, context.Owner);
        var previous = installedBefore.FirstOrDefault(value =>
            value.SlotKind == "User" && value.PackageIdentity == artifact.Manifest.PackageName);
        previous ??= installedBefore.Length == 1 && installedBefore[0].PackageIdentity.Length == 0 && installedBefore[0].ProjectId == project.Id
            ? installedBefore[0] : null;
        var installAuthority = await CaptureInstallationAuthorityAsync(context, host.Value);
        if (installAuthority == null) { state.Notice = Loc.GetString("nc-meta-error-access"); return; }
        var outcome = await TryCommitInstallationSwapAsync(host.Value, context.User, context.Terminal, artifact, bindings, "User", previous,
            guard => _persistence.InstallAsync(request.RequestId, target, context.Owner, project, release,
                bindings, autoStart: false, context.Epoch, context.Time, limits.Value.StorageBytes, limits.Value.Slots,
                activationGuard: guard, authority: installAuthority), () => IsCurrent(context));
        state.Notice = Loc.GetString(outcome.MessageKey);
    }

    private async Task<(bool Declared, MuiVerifiedApplication? Application)> ReadApplicationAsync(NCMetaRelease release, VerifiedArtifact artifact)
    {
        var contract = await _persistence.ReadReleaseResourceAsync(release, "app.json");
        if (contract == null)
        {
            var manifest = await _persistence.ReadReleaseResourceAsync(release, "manifest.json");
            if (manifest != null)
            {
                try
                {
                    using var json = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 16 });
                    if (json.RootElement.TryGetProperty("app", out var app)) contract = app.GetRawText();
                }
                catch (JsonException) { return (true, null); }
            }
        }
        if (contract == null) return (false, null);
        if (release.Exports == null || !MuiContractParser.TryParse(contract, out var parsed, out _)) return (true, null);
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in parsed!.Views.Values.Select(view => view.Document).Concat(parsed.Resources.Where(path =>
                     path.EndsWith(".mui", StringComparison.Ordinal))).Distinct(StringComparer.Ordinal))
        {
            var document = await _persistence.ReadReleaseResourceAsync(release, path);
            if (document == null) return (true, null);
            documents.Add(path, document);
        }
        var stylePath = release.Resources?.FirstOrDefault(value => value.Path.EndsWith(".mstyle", StringComparison.Ordinal))?.Path;
        var styles = stylePath == null ? string.Empty : await _persistence.ReadReleaseResourceAsync(release, stylePath) ?? string.Empty;
        var digest = MuiVerifiedApplication.ComputePackageDigest(artifact.Digest, contract, documents, styles);
        return MuiVerifiedApplication.TryAdmit(artifact, release.Exports, contract, documents, styles, digest, out var application, out _)
            ? (true, application) : (true, null);
    }

    private bool HandleDebug(Context context, EntityUid host, Guid installationId, NCMetaWorkspaceRequestMessage request, NCMetaWorkspaceState state)
    {
        if (!IsCurrent(context) || !_runtime.TryGetInstallation(host, context.Owner, installationId, out var runtime)) return false;
        var key = (context.Terminal, context.User);
        if (request.Operation == NCMetaWorkspaceOperation.DebugAttach)
        {
            if (_debug.Count >= 64 && !_debug.ContainsKey(key)) return false;
            _debug[key] = (host, runtime.Generation);
            return true;
        }
        if (!_debug.TryGetValue(key, out var attached) || attached.Host != host || attached.Generation != runtime.Generation) return false;
        if (request.Operation == NCMetaWorkspaceOperation.DebugDetach)
        {
            _runtime.TryDebug(host, context.User, installationId, process => !process.DebugPaused || process.TryContinue());
            _debug.Remove(key);
            return true;
        }
        return _runtime.TryDebug(host, context.User, installationId, process =>
        {
            switch (request.Operation)
            {
                case NCMetaWorkspaceOperation.DebugPause: return process.TryPause(process.NowMs);
                case NCMetaWorkspaceOperation.DebugContinue: return process.TryContinue();
                case NCMetaWorkspaceOperation.DebugStepInto: return process.TryStepInto();
                case NCMetaWorkspaceOperation.DebugStepOver: return process.TryStepOver();
                case NCMetaWorkspaceOperation.DebugStepOut: return process.TryStepOut();
                case NCMetaWorkspaceOperation.DebugEvaluate:
                    state.Notice = process.TryEvaluateDebug(request.Source, out var display) ? display : Loc.GetString("nc-meta-debug-evaluation-rejected");
                    return true;
                case NCMetaWorkspaceOperation.DebugSetBreakpoint:
                    var debug = runtime.Artifact.GetDebugInfo();
                    var file = debug?.Files.FirstOrDefault(value => value.Path == request.FileId.ToString("D"));
                    var span = file == null ? null : debug!.Spans.Where(value => value.FileId == file.Id && value.Utf8Start >= request.SourceOffset)
                        .OrderBy(value => value.Utf8Start).FirstOrDefault();
                    return span != null && process.TrySetBreakpoint(span.FunctionId, span.PcOffset);
                default: return false;
            }
        });
    }

    private void PopulateDebug(Context context, NCMetaWorkspaceState state)
    {
        if (!_debug.TryGetValue((context.Terminal, context.User), out var attached)) return;
        if (!_runtime.TryGetGeneration(attached.Host, context.Owner, attached.Generation, out var runtime))
        { _debug.Remove((context.Terminal, context.User)); return; }
        state.DebugAttached = true;
        state.DebugPaused = runtime.Process.DebugPaused;
        using var snapshot = runtime.Process.GetDebugSnapshot(64);
        foreach (var frame in snapshot.Frames)
        {
            var function = runtime.Artifact.GetFunction(frame.FunctionId);
            state.DebugVariables.Add(function.Name + " @" + frame.InstructionOffset);
            var names = runtime.Artifact.GetDebugInfo()?.Locals.Where(value => value.FunctionId == frame.FunctionId)
                .ToDictionary(value => value.LocalId, value => value.Name);
            for (var index = 0; index < frame.Locals.Length; index++)
                state.DebugVariables.Add((names?.GetValueOrDefault(index) ?? "local" + index) + " = " + frame.Locals[index].Display);
        }
    }
}
