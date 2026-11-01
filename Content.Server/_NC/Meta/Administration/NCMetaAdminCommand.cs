// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Language;
using Content.Meta.Verification;
using Content.Server.Administration;
using Content.Server.Administration.Managers;
using Content.Server._NC.Meta.Authority;
using Content.Server._NC.Meta.Runtime;
using Content.Shared.Administration;
using Content.Shared._NC.Meta;
using Robust.Shared.Asynchronous;
using Robust.Shared.Configuration;
using Robust.Shared.Console;

namespace Content.Server._NC.Meta.Administration;

[AdminCommand(AdminFlags.Admin)]
public sealed partial class NCMetaAdminCommand : IConsoleCommand
{
    [Dependency] private IEntityManager _entities = default!;
    [Dependency] private IAdminManager _admins = default!;
    [Dependency] private IConfigurationManager _configuration = default!;
    [Dependency] private ITaskManager _tasks = default!;
    public string Command => "ncmeta";
    public string Description => Loc.GetString("nc-meta-admin-description");
    public string Help => Loc.GetString("nc-meta-admin-help");
    private bool Authorized(IConsoleShell shell) => shell.Player == null || _admins.HasAdminFlag(shell.Player, AdminFlags.Admin);

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (!Authorized(shell)) { shell.WriteError(Loc.GetString("nc-meta-error-access")); return; }
        if (args.Length is < 1 or > 16) { shell.WriteError(Help); return; }
        var runtime = _entities.System<NCMetaRuntimeSystem>();
        if (args[0] is "status" or "repro")
        {
            EntityUid? host = null;
            if (args.Length > 2 || args.Length == 2 && (!NetEntity.TryParse(args[1], out var network) ||
                    !_entities.TryGetEntity(network, out host) || host == null)) { shell.WriteError(Help); return; }
            var status = runtime.GetAdministrativeSnapshot(host);
            shell.WriteLine(Loc.GetString("nc-meta-admin-summary", ("enabled", status.Enabled), ("valid", status.PolicyValid),
                ("processes", status.Processes), ("hosts", status.Hosts), ("pending", status.PendingIntents), ("blocked", status.RevokedArtifacts)));
            if (args[0] == "repro")
                shell.WriteLine(JsonSerializer.Serialize(new
                {
                    Format = 1, CompilerBuild = MetaCompiler.BuildId, CatalogHash = MetaApiCatalog.Default.Hash,
                    VerifierPolicy = MetaVerifier.PolicyVersion, SourceIncluded = false,
                    ServerBuild = typeof(NCMetaRuntimeSystem).Assembly.ManifestModule.ModuleVersionId,
                    CoreBuild = typeof(MetaCompiler).Assembly.ManifestModule.ModuleVersionId,
                    ProcessPageLimit = 16, AuditPageLimit = 64,
                    Snapshot = status with { Page = status.Page.Take(16).ToArray(), Audit = status.Audit.TakeLast(64).ToArray() },
                }));
            return;
        }
        if (args[0] == "enabled" && args.Length == 2 && bool.TryParse(args[1], out var enabled))
        {
            _configuration.SetCVar(NCMetaCVars.Enabled, enabled);
            SaveConfiguration(shell); return;
        }
        if (args[0] is "block" or "quarantine" or "unblock" && args.Length == 2)
        {
            if (!runtime.TrySetOperationalArtifactBlock(args[1], args[0] != "unblock"))
            { shell.WriteError(Loc.GetString("nc-meta-error-input")); return; }
            SaveConfiguration(shell); return;
        }
        if (args[0] == "stop" && args.Length == 3)
        {
            var count = 0;
            if (args[1] == "process" && Guid.TryParse(args[2], out var generation))
                count = runtime.TryStopAdministrativeProcess(generation) ? 1 : 0;
            else if (args[1] == "principal") count = runtime.StopAdministrativePrincipal(args[2]);
            else if (args[1] == "device" && NetEntity.TryParse(args[2], out var network) &&
                     _entities.TryGetEntity(network, out var entity) && entity is { } uid)
            { count = runtime.GetProcessCount(uid); runtime.StopHost(uid); }
            else { shell.WriteError(Help); return; }
            shell.WriteLine(Loc.GetString("nc-meta-admin-stopped", ("count", count))); return;
        }
        if (args[0] == "revoke" && args.Length >= 3 && Guid.TryParse(args[1], out var installation))
        {
            _ = RevokeAsync(shell, installation, string.Join(' ', args.Skip(2)));
            shell.WriteLine(Loc.GetString("nc-meta-admin-pending")); return;
        }
        shell.WriteError(Help);
    }
    private void SaveConfiguration(IConsoleShell shell)
    {
        try { _configuration.SaveToFile(); shell.WriteLine(Loc.GetString("nc-meta-admin-policy-saved")); }
        catch (Exception) { shell.WriteError(Loc.GetString("nc-meta-admin-policy-unsaved")); }
    }
    private async Task RevokeAsync(IConsoleShell shell, Guid installation, string reason)
    {
        try
        {
            var outcome = await _entities.System<NCMetaAuthoritySystem>().TryAdministrativeRevokeAsync(Guid.NewGuid(),
                installation, shell.Player?.UserId.ToString() ?? "server-console", reason, () => Authorized(shell));
            _tasks.RunOnMainThread(() => { if (Authorized(shell)) shell.WriteLine(Loc.GetString(outcome.MessageKey)); });
        }
        catch (Exception)
        {
            _tasks.RunOnMainThread(() => { if (Authorized(shell)) shell.WriteError(Loc.GetString("nc-meta-error-storage")); });
        }
    }
}
