// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Content.Meta.Language;
using Content.Meta.Verification;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Meta.UI;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server._NC.Meta.Projects;

public sealed partial class NCMetaWorkspaceSystem
{
    private sealed record LanguageTransfer(Guid Id, Context Context, Guid Project, long Revision, string Stamp,
        byte[] Bytes, string Hash, NCMetaDependencyRef[] Dependencies, TimeSpan Expires);
    private readonly Dictionary<(EntityUid Terminal, EntityUid User), LanguageTransfer> _languageTransfers = [];
    private readonly Dictionary<(EntityUid Terminal, EntityUid User), Guid> _languageBuildTokens = [];
    private readonly System.Threading.SemaphoreSlim _languageHeaderBuilds = new(2, 2);

    private void InitializeLanguageSymbols()
    {
        _authority.ManagementAuthorityChanged += InvalidateLanguageTransfers;
        _marketplace.EntitlementChanged += OnLanguageEntitlementChanged;
        _marketplace.LicenseDeviceRevoked += OnLanguageDeviceRevoked;
    }
    private void ShutdownLanguageSymbols()
    {
        _authority.ManagementAuthorityChanged -= InvalidateLanguageTransfers;
        _marketplace.EntitlementChanged -= OnLanguageEntitlementChanged;
        _marketplace.LicenseDeviceRevoked -= OnLanguageDeviceRevoked;
        _languageTransfers.Clear();
        _languageBuildTokens.Clear();
    }
    private void OnLanguageEntitlementChanged(Guid product, int major, string owner) => InvalidateLanguageTransfers();
    private void OnLanguageDeviceRevoked(string device) => InvalidateLanguageTransfers();
    private void InvalidateLanguageTransfers()
    {
        foreach (var draft in _drafts.Values.ToArray())
            SendDraft(draft, new(draft.Request.Id) { Rejected = true, Complete = true });
        ClearDrafts();
        foreach (var transfer in _languageTransfers.Values.ToArray())
            if (_ui.IsUiOpen(transfer.Context.Terminal, NCMetaUiKey.Workspace, transfer.Context.User))
                _ui.ServerSendUiMessage(transfer.Context.Terminal, NCMetaUiKey.Workspace,
                    new NCMetaWorkspaceUpdateMessage(new() { Partial = true,
                        LanguageSymbols = new(transfer.Project, transfer.Revision, transfer.Stamp, transfer.Id, 0, 0, "", "", true, Revoked: true) }),
                    transfer.Context.User);
        _languageTransfers.Clear();
        _languageBuildTokens.Clear();
    }
    private static string LanguageRevisionStamp(NCMetaProject project) => MetaSemanticHeaders.RevisionStamp(project.Files
        .Select(file => new MetaSemanticRevision(file.Id.ToString("D"), file.Path, file.Revision, file.Digest)));

    /// <summary>Trusted native read API; uses the identical open-workspace, membership and per-page entitlement checks as the BUI.</summary>
    public async Task<NCMetaLanguageSymbols?> ReadLanguageSymbolsAsync(EntityUid terminal, EntityUid user, Guid projectId,
        long expectedRevision, Guid transferId = default, int utf8Offset = 0, int chunkBytes = 32768)
    {
        var owner = await OnMainAsync(() => TryComp<Content.Shared._NC.Meta.NCMetaTerminalComponent>(terminal, out _) &&
            _devices.TryGetEntry(terminal, out var entry) ? entry.Owner : null);
        if (owner == null || await _authority.CaptureManagementAsync(user, owner) == null) return null;
        var context = await OnMainAsync(() => CaptureWorkspaceContext(terminal, user));
        if (context == null) return null;
        var device = await _persistence.ReadDeviceAsync(context.DeviceId);
        if (device == null || device.Owner != context.Owner || !device.Projects.Contains(projectId)) return null;
        var project = await _persistence.ReadProjectAsync(projectId, context.Owner);
        return project == null ? null : await ReadLanguagePageAsync(context, project,
            new(NCMetaWorkspaceOperation.ReadLanguageSymbols, Guid.NewGuid(), baseRevision: expectedRevision,
                projectId: projectId, transferId: transferId, utf8Offset: utf8Offset, chunkBytes: chunkBytes));
    }

    private async Task LanguageSymbolsAsync(Context context, NCMetaProject project, NCMetaWorkspaceRequestMessage request)
    {
        var result = await ReadLanguagePageAsync(context, project, request);
        await OnMainAsync(() =>
        {
            if (!_ui.IsUiOpen(context.Terminal, NCMetaUiKey.Workspace, context.User)) return false;
            _ui.ServerSendUiMessage(context.Terminal, NCMetaUiKey.Workspace, new NCMetaWorkspaceUpdateMessage(new()
                { Partial = true, AcknowledgedRequestId = request.RequestId, LanguageSymbols = result }), context.User);
            return true;
        });
    }

    private async Task<NCMetaLanguageSymbols> ReadLanguagePageAsync(Context context, NCMetaProject project, NCMetaWorkspaceRequestMessage request)
    {
        var key = (context.Terminal, context.User);
        LanguageTransfer? transfer = null;
        if (request.BaseRevision == project.Revision && await OnMainAsync(() => IsCurrent(context, afterClaim: true) &&
                !_nativeTools.ContainsKey(key)))
        {
            transfer = await OnMainAsync(() =>
            {
                foreach (var expired in _languageTransfers.Where(value => value.Value.Expires <= _timing.CurTime ||
                             value.Value.Context.Epoch != context.Epoch).Select(value => value.Key).ToArray()) _languageTransfers.Remove(expired);
                return request.TransferId != Guid.Empty && _languageTransfers.TryGetValue(key, out var prior) && prior.Id == request.TransferId &&
                    prior.Project == project.Id && prior.Revision == project.Revision && prior.Stamp == LanguageRevisionStamp(project) ? prior : null;
            });
            if (request.TransferId == Guid.Empty && await _languageHeaderBuilds.WaitAsync(0))
            {
                var buildToken = await OnMainAsync(() =>
                {
                    if (!IsCurrent(context, afterClaim: true) || !_languageBuildTokens.ContainsKey(key) && _languageBuildTokens.Count >= 16)
                        return Guid.Empty;
                    var token = Guid.NewGuid(); _languageBuildTokens[key] = token; return token;
                });
                try
                {
                    if (buildToken == Guid.Empty) return new(project.Id, project.Revision, LanguageRevisionStamp(project), Guid.Empty, 0, 0, "", "", true, Revoked: true);
                    var inferred = project.Files.FirstOrDefault(file => file.Path.EndsWith(".meta", StringComparison.Ordinal));
                    var text = inferred == null ? null : await _persistence.ReadSourceAsync(project, inferred.Id);
                    var root = text == null ? null : await Task.Run(() => MetaCompiler.InferRootModule(new(inferred!.Id.ToString("D"), text)));
                    var package = root == null ? null : await ResolveCompilationPackageAsync(context, project, root);
                    if (package != null)
                    {
                        var json = await Task.Run(() =>
                        {
                            var headers = new List<MetaSemanticLibraryHeader>();
                            foreach (var dependency in package.Libraries)
                                headers.Add(MetaSemanticHeaders.Project(MetaVerifier.Verify(dependency.Artifact,
                                    MetaApiCatalog.Default.CreateVerificationCatalog(Content.Meta.Bytecode.MetaBytecodeLoader.Load(dependency.Artifact))), dependency.Exports));
                            return JsonSerializer.Serialize(new MetaSemanticEnvironment(package.RootModule, package.Name, package.Version, package.Library, headers.ToArray()));
                        });
                        var bytes = Utf8.GetBytes(json);
                        if (bytes.Length <= MetaSemanticHeaders.MaximumBytes)
                        {
                            var created = new LanguageTransfer(Guid.NewGuid(), context, project.Id, project.Revision, LanguageRevisionStamp(project),
                                bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), package.Dependencies,
                                await OnMainAsync(() => _timing.CurTime + TimeSpan.FromSeconds(10)));
                            transfer = await OnMainAsync(() =>
                            {
                                if (!IsCurrent(context, afterClaim: true) || _languageBuildTokens.GetValueOrDefault(key) != buildToken ||
                                    !_languageTransfers.ContainsKey(key) && _languageTransfers.Count >= 16) return null;
                                _languageTransfers[key] = created; return created;
                            });
                        }
                    }
                }
                finally
                {
                    await OnMainAsync(() => _languageBuildTokens.GetValueOrDefault(key) == buildToken && _languageBuildTokens.Remove(key));
                    _languageHeaderBuilds.Release();
                }
            }
        }
        // Every page revalidates commercial entitlement after all DB/bytecode work. A transfer ID is never authority.
        var current = transfer == null ? null : await _persistence.ReadProjectAsync(project.Id, context.Owner);
        if (transfer == null || current == null || current.Revision != transfer.Revision || LanguageRevisionStamp(current) != transfer.Stamp ||
            !await RevalidateBuildDependenciesAsync(context, transfer.Dependencies)) transfer = null;
        return await OnMainAsync(() =>
        {
            NCMetaLanguageSymbols result;
            if (transfer == null || !IsCurrent(context, afterClaim: true) || transfer.Expires <= _timing.CurTime ||
                !_languageTransfers.TryGetValue(key, out var live) || !ReferenceEquals(transfer, live) || request.Utf8Offset < 0 || request.Utf8Offset > transfer.Bytes.Length)
            {
                if (_languageTransfers.TryGetValue(key, out var existing) &&
                    (ReferenceEquals(transfer, existing) || request.TransferId == existing.Id)) _languageTransfers.Remove(key);
                result = new(project.Id, project.Revision, LanguageRevisionStamp(project), request.TransferId, 0, 0, "", "", true, Revoked: true);
            }
            else
            {
                var start = request.Utf8Offset;
                bool Boundary(int offset) => offset == transfer.Bytes.Length || (transfer.Bytes[offset] & 0xC0) != 0x80;
                if (!Boundary(start)) return new NCMetaLanguageSymbols(project.Id, project.Revision, transfer.Stamp,
                    request.TransferId, 0, 0, "", "", true, Revoked: true);
                var end = Math.Min(transfer.Bytes.Length, start + Math.Clamp(request.ChunkBytes, 1, 32768));
                while (!Boundary(end)) end--;
                if (end == start && end != transfer.Bytes.Length) return new NCMetaLanguageSymbols(project.Id, project.Revision,
                    transfer.Stamp, request.TransferId, 0, 0, "", "", true, Revoked: true);
                result = new(project.Id, project.Revision, transfer.Stamp, transfer.Id, start, transfer.Bytes.Length,
                    transfer.Hash, Utf8.GetString(transfer.Bytes.AsSpan(start, end - start)), end == transfer.Bytes.Length);
            }
            return result;
        });
    }
}
