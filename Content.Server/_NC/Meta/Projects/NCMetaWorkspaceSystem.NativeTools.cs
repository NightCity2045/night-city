// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Runtime;
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Meta;
using Content.Shared._NC.Meta.UI;
using Content.Shared.UserInterface;
using Content.Shared.Verbs;

namespace Content.Server._NC.Meta.Projects;

public sealed partial class NCMetaWorkspaceSystem
{
    private readonly Dictionary<(EntityUid Terminal, EntityUid User), string> _nativeTools = [];
    private static readonly string[] NativeToolViews = ["installer", "recovery", "hardware", "diagnostics"];

    private void InitializeNativeTools()
    {
        SubscribeLocalEvent<NCMetaTerminalComponent, GetVerbsEvent<AlternativeVerb>>(OnNativeToolsVerbs);
        SubscribeLocalEvent<NCMetaTerminalComponent, ComponentShutdown>(OnNativeToolsShutdown);
    }

    private void OnNativeToolsVerbs(Entity<NCMetaTerminalComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !_devices.CanManage(ent.Owner, args.User)) return;
        var actor = args.User;
        // One service panel switches between installer, recovery, hardware monitor and diagnostics itself.
        args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-panel-open"),
            Act = () => TryOpenNativeTools(ent.Owner, actor, NativeToolViews[0]) });
        if (ent.Comp.AllowEditor)
            args.Verbs.Add(new AlternativeVerb { Text = Loc.GetString("nc-meta-domain-workspace"),
                Act = () => TryOpenDevelopmentWorkspace(ent.Owner, actor) });
    }

    /// <summary>Whether the device offers the source editor; servers only offer the service panel.</summary>
    public bool CanOpenDevelopmentWorkspace(EntityUid terminal) =>
        TryComp<NCMetaTerminalComponent>(terminal, out var component) && component.AllowEditor;

    /// <summary>Opens existing native service operations without opening the source editor.</summary>
    public bool TryOpenNativeTools(EntityUid terminal, EntityUid user, string view)
    {
        if (!NativeToolViews.Contains(view, StringComparer.Ordinal) || !_devices.CanManage(terminal, user) ||
            !HasComp<NCMetaTerminalComponent>(terminal) ||
            _nativeTools.Count >= 128 && !_nativeTools.ContainsKey((terminal, user))) return false;
        _nativeTools[(terminal, user)] = view;
        if (!_ui.TryOpenUi(terminal, NCMetaUiKey.Workspace, user))
        {
            _nativeTools.Remove((terminal, user));
            return false;
        }
        _ = BeginAuthorizedAsync(terminal, user, new(NCMetaWorkspaceOperation.OpenNativeTools, Guid.NewGuid(), source: view));
        return true;
    }

    public bool TryOpenDevelopmentWorkspace(EntityUid terminal, EntityUid user)
    {
        if (!_devices.CanManage(terminal, user) || !CanOpenDevelopmentWorkspace(terminal)) return false;
        _nativeTools.Remove((terminal, user));
        if (!_ui.TryOpenUi(terminal, NCMetaUiKey.Workspace, user)) return false;
        _ = BeginAuthorizedAsync(terminal, user, new(NCMetaWorkspaceOperation.Open, Guid.NewGuid()));
        return true;
    }

    /// <summary>Maintenance moved from the right-click menu into the hardware view; Source is "enter" or "leave".</summary>
    private void SetMaintenanceFromPanel(Context context, NCMetaWorkspaceRequestMessage request)
    {
        if (!_devices.TryResolve(request.DeviceId, out var target) || request.Source is not ("enter" or "leave") ||
            !_runtime.CanReach(context.Terminal, target.Entity)) return;
        _hardware.TrySetMaintenance(target.Entity, context.User, request.Source == "enter");
    }

    private void OnNativeToolsShutdown(Entity<NCMetaTerminalComponent> ent, ref ComponentShutdown args)
    {
        foreach (var key in _nativeTools.Keys.Where(value => value.Terminal == ent.Owner).ToArray()) _nativeTools.Remove(key);
    }

    private void SelectNativeTools(Context context, NCMetaWorkspaceRequestMessage request)
    {
        if (request.Source == "editor")
        {
            if (CanOpenDevelopmentWorkspace(context.Terminal)) _nativeTools.Remove((context.Terminal, context.User));
            return;
        }
        if (NativeToolViews.Contains(request.Source, StringComparer.Ordinal) &&
            (_nativeTools.Count < 128 || _nativeTools.ContainsKey((context.Terminal, context.User))))
            _nativeTools[(context.Terminal, context.User)] = request.Source;
    }

    private async Task<NCMetaProject?> PrepareStockApplicationAsync(Context context, NCMetaDeviceRecord device,
        NCMetaWorkspaceRequestMessage request, NCMetaWorkspaceState state)
    {
        var template = await OnMainAsync(() => IsStockTemplate(request.Source) && _catalog.TryGet(request.Source, out var found) ? found : null);
        if (template == null) { state.Notice = Loc.GetString("nc-meta-error-input"); return null; }
        var cloneId = NCMetaPersistenceSystem.StableGuid("stock-clone:" + request.RequestId);
        var cloned = await _persistence.CloneProjectAsync(cloneId, device, template.Name, template.Files,
            context.Epoch, context.Time, authority: WorkspaceAuthority(context));
        if (!cloned.Success) { state.Notice = Loc.GetString(cloned.MessageKey); return null; }
        var projectId = NCMetaPersistenceSystem.StableGuid("project-clone:" + context.Owner + ":" + cloneId);
        var project = await _persistence.ReadProjectAsync(projectId, context.Owner);
        if (project == null) return null;
        await OnMainAsync(() => { SelectNativeTools(context, new(NCMetaWorkspaceOperation.OpenNativeTools, request.RequestId, source: "installer")); return true; });
        await CompileAsync(context, project, new(NCMetaWorkspaceOperation.Compile,
            NCMetaPersistenceSystem.StableGuid("stock-compile:" + request.RequestId)), state);
        return await _persistence.ReadProjectAsync(projectId, context.Owner);
    }

    private static bool IsStockTemplate(string id) => id.StartsWith("meta-stock-", StringComparison.Ordinal) ||
        id.StartsWith("meta-tutorial-", StringComparison.Ordinal);

    private void PopulateNativeTools(Context context, NCMetaWorkspaceState state)
    {
        if (!_nativeTools.TryGetValue((context.Terminal, context.User), out var view))
        {
            // A device without the editor always opens the service panel, however its window was opened.
            if (CanOpenDevelopmentWorkspace(context.Terminal)) return;
            view = NativeToolViews[0];
        }
        var hardware = new List<NCMetaHardwareSummary>();
        foreach (var entry in _devices.GetOwned(context.Owner).Take(128))
        {
            if (hardware.Count >= 64) break;
            if (!_devices.CanManage(entry.Entity, context.User) ||
                !_runtime.CanReach(context.Terminal, entry.Entity) ||
                !TryComp<NCMetaHostComponent>(entry.Entity, out var host) || !_hardwareProfiles.TryIndex(host.Profile, out var profile)) continue;
            TryComp<NCMetaHardwareStateComponent>(entry.Entity, out var physical);
            _runtime.TryGetHardwareUsage(entry.Id, out var usage);
            _runtime.TryGetLinkUsage(entry.Entity, out var linkReserve, out var linkCapacity);
            var power = physical?.Power switch
            {
                NCMetaPowerState.Off => "off", NCMetaPowerState.Booting => "booting", NCMetaPowerState.On => "on",
                NCMetaPowerState.Brownout => "brownout", NCMetaPowerState.ShuttingDown => "shutting-down", _ => "unknown",
            };
            var health = physical?.Health switch
            {
                NCMetaHealthState.Operational => "operational", NCMetaHealthState.Degraded => "degraded",
                NCMetaHealthState.HardwareFault => "hardware-fault", NCMetaHealthState.FirmwareFault => "firmware-fault",
                NCMetaHealthState.Maintenance => "maintenance", _ => "unknown",
            };
            hardware.Add(new(entry.Id, Name(entry.Entity), host.Profile.Id, "nc-meta-tools-power-" + power,
                "nc-meta-tools-health-" + health, _devices.IsPowered(entry.Entity), physical?.Maintenance ?? false,
                _runtime.GetProcessCount(entry.Entity), profile.MaxProcesses, _runtime.GetReservedMemoryBytes(entry.Entity),
                profile.MemoryBytes, usage?.WindowGas ?? 0, profile.GasPerWindow, physical?.ThrottlePermille ?? 0,
                physical?.LoadFactor ?? 0, physical?.AppliedWatts ?? 0, physical?.TemperatureKelvin, linkReserve, linkCapacity));
        }
        state.NativeTools = new(view, hardware);
        state.Files.Clear(); state.Contents.Clear(); state.DebugVariables.Clear();
        state.Templates = state.Templates.Where(value => IsStockTemplate(value.Id)).ToList();
    }
}
