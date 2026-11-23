// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Server._NC.Meta.Persistence;
using Content.Shared._NC.Meta.UI;

namespace Content.Server._NC.Meta.Projects;

public sealed partial class NCMetaWorkspaceSystem
{
    /// <summary>System programs of the computer's own operating system: shipped as stock programs, started from the desktop.</summary>
    private static readonly Dictionary<string, (string Package, string View)> SystemApplications = new(StringComparer.Ordinal)
    {
        ["meta-stock-citinet-browser"] = ("citinet.browser", "main"),
    };

    /// <summary>
    /// Opens a system program, installing it first if this computer does not have it yet. Nobody picks devices or grants
    /// anything by hand: every link of a system program goes to the computer it runs on.
    /// </summary>
    private async Task OpenSystemApplicationAsync(Context context, NCMetaDeviceRecord device, NCMetaWorkspaceRequestMessage request,
        NCMetaWorkspaceState state)
    {
        if (!SystemApplications.TryGetValue(request.Source, out var system)) { state.Notice = Loc.GetString("nc-meta-error-input"); return; }
        // Preparing a stock program makes its copy the active project; the player's own project is put back afterwards.
        var previous = await _persistence.ReadProjectAsync(device.ActiveProject, context.Owner);
        var installed = await _persistence.ReadInstallationsAsync(context.DeviceId, context.Owner);
        var existing = installed.FirstOrDefault(value => value.SlotKind == "User" && value.PackageIdentity == system.Package);
        NCMetaProject? project;
        if (existing == null)
        {
            project = await PrepareStockApplicationAsync(context, device, request, state);
            if (project == null) return;
            // Preparing leaves the service panel on its installer view; a computer goes back to where it was.
            await OnMainAsync(() => { SelectNativeTools(context, new(NCMetaWorkspaceOperation.OpenNativeTools, request.RequestId, source: "editor")); return true; });
            context = await FreshClockAsync(context);
            await RuntimeRequestAsync(context, project, new(NCMetaWorkspaceOperation.Install, Guid.NewGuid(),
                projectId: project.Id, deviceId: context.DeviceId), state);
            context = await FreshClockAsync(context);
            if (previous != null && await _persistence.ReadDeviceAsync(context.DeviceId) is { } current && current.ActiveProject != previous.Id)
                await _persistence.ManageProjectAsync(NCMetaWorkspaceOperation.SelectProject, Guid.NewGuid(), current, previous,
                    Guid.Empty, 0, "", context.Epoch, context.Time, WorkspaceAuthority(context));
        }
        else project = await _persistence.ReadProjectAsync(existing.ProjectId, context.Owner);
        if (project == null) { state.Notice = Loc.GetString("nc-meta-error-action"); return; }

        var installation = await SelectInstallationAsync(context.DeviceId, context.Owner, project.Id, Guid.Empty);
        if (installation == null) return;
        var host = await OnMainAsync<EntityUid?>(() => _devices.TryResolve(context.DeviceId, out var found) ? found.Entity : null);
        if (host == null) return;
        context = await FreshClockAsync(context);
        var running = await OnMainAsync(() => _runtime.TryGetInstallation(host.Value, context.Owner, installation.Id, out _));
        if (!running)
            await RuntimeRequestAsync(context, project, new(NCMetaWorkspaceOperation.Start, Guid.NewGuid(),
                projectId: project.Id, deviceId: context.DeviceId, installationId: installation.Id), state);
        context = await FreshClockAsync(context);
        await RuntimeRequestAsync(context, project, new(NCMetaWorkspaceOperation.OpenApplication, Guid.NewGuid(), source: system.View,
            projectId: project.Id, deviceId: context.DeviceId, installationId: installation.Id), state);
    }

    /// <summary>
    /// Each durable write is stamped with the world clock, and the database refuses a stamp older than its own. A chain of
    /// writes (copy, build, install, start) outlives the time the request was admitted at, so every step takes a new one.
    /// </summary>
    private Task<Context> FreshClockAsync(Context context) => OnMainAsync(() =>
        _persistence.TryCaptureTime(out var epoch, out var time) ? context with { Epoch = epoch, Time = time } : context);
}
