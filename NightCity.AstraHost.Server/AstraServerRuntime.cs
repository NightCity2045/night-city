// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Reflection;
using AstraGraph.Editor.Protocol;
using AstraGraph.Robust.Server;
using Content.Shared._NC.AstraGraph;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Log;

namespace Content.Server._NC.AstraGraph;

/// <summary>
/// Server calls that used to sit in the content assembly.
/// </summary>
public static class AstraServerRuntime
{
    private const string BindingsNamespace = "Content.Shared._NC.AstraGraph.Bindings";

    public static string? IssueLaunchToken(object session)
    {
        var system = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<ServerAstraGraphSystem>();
        return system.AuthoringService.IssueLaunchToken(session)?.Token;
    }

    public static void InvalidateSession(object session)
    {
        var system = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<ServerAstraGraphSystem>();
        system.PermissionProvider.InvalidateSession(session);
    }

    public static async Task<AstraWireReply?> HandleAsync(string kind, byte[] payload)
    {
        try
        {
            var incoming = AstraAuthoringCodec.Deserialize(kind, payload);
            if (incoming == null)
                return Error("unsupported_message", $"Message kind '{kind}' is not supported.");

            var system = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<ServerAstraGraphSystem>();
            var response = await system.AuthoringService.MessageHandler.HandleAsync(incoming, CancellationToken.None);
            if (response == null)
                return null;

            return new AstraWireReply(response.Kind, AstraAuthoringCodec.Serialize(response));
        }
        catch (Exception ex)
        {
            Logger.GetSawmill("astra").Error($"AstraGraph authoring request failed: {ex.Message}");
            return Error("internal_error", "AstraGraph authoring request failed.");
        }
    }

    public static void WhenHostReady(Action ready)
    {
        var manager = IoCManager.Resolve<IEntitySystemManager>();
        if (TryGetReady(out _))
        {
            ready();
            return;
        }

        void OnLoaded(object? sender, SystemChangedArgs args)
        {
            if (args.System is not ServerAstraGraphSystem)
                return;

            manager.SystemLoaded -= OnLoaded;
            ready();
        }

        manager.SystemLoaded += OnLoaded;
    }

    public static void IndexContentBindings()
    {
        if (!TryGetReady(out var system))
            return;

        var shared = FindAssembly("Content.Shared");
        if (shared == null)
            return;

        system.Facade.Catalog.IndexAssembly(shared, IsBindingsType);
    }

    public static string StartupReport()
    {
        var system = IoCManager.Resolve<IEntitySystemManager>().GetEntitySystem<ServerAstraGraphSystem>();
        var discovered = system.BootstrapLoader.DiscoverAll();
        var active = 0;
        foreach (var graph in discovered)
        {
            if (system.Host.GetProgram(graph.Id) != null)
                active++;
        }

        var bindings = 0;
        foreach (var _ in system.Facade.Catalog.Search(string.Empty))
            bindings++;

        return
            "AstraGraph Night City host\n" +
            $"Graphs discovered: {discovered.Count}\n" +
            $"Graphs active: {active}\n" +
            "Authoring: enabled\n" +
            "DevHost: off\n" +
            $"Binding catalog methods: {bindings}";
    }

    private static AstraWireReply Error(string code, string detail)
    {
        var message = new ErrorMsg { Code = code, Detail = detail };
        return new AstraWireReply(message.Kind, AstraAuthoringCodec.Serialize(message));
    }

    private static bool IsBindingsType(Type type)
    {
        return type.Namespace != null
               && (type.Namespace == BindingsNamespace
                   || type.Namespace.StartsWith(BindingsNamespace + ".", StringComparison.Ordinal));
    }

    private static bool TryGetReady(out ServerAstraGraphSystem system)
    {
        if (!IoCManager.Resolve<IEntitySystemManager>().TryGetEntitySystem(out system!)
            || system.Facade == null)
        {
            system = null!;
            return false;
        }

        return true;
    }

    private static Assembly? FindAssembly(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.GetName().Name == name)
                return assembly;
        }

        return null;
    }
}
