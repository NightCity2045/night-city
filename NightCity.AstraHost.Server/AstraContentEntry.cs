// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.IO;
using System.Reflection;
using AstraGraph.HotReload;
using AstraGraph.Persistence;
using AstraGraph.Robust.Server;
using AstraGraph.Runtime.Security;
using Content.Shared._NC.AstraGraph;
using Robust.Shared.IoC;

namespace Content.Server._NC.AstraGraph;

/// <summary>
/// Loads the server runtime and builds host options. Content only calls this type.
/// </summary>
public static class AstraContentEntry
{
    public static void Load(IDependencyCollection dependencies)
    {
        AstraGraphAssemblyLoad.Load(
            dependencies,
            typeof(ServerAstraGraphSystem).Assembly,
            typeof(ServerAstraGraphSystem).BaseType?.Assembly);
    }

    public static void InstallSchemas(Robust.Shared.GameObjects.IComponentFactory factory, IDependencyCollection dependencies)
    {
        AstraSchemaStartup.Install(factory, dependencies);
    }

    public static void Register(IDependencyCollection dependencies)
    {
        dependencies.Register<IAstraServerHostConfiguration, NightCityAstraServerHostConfiguration>();
    }
}

public sealed class NightCityAstraServerHostConfiguration : IAstraServerHostConfiguration
{
    private readonly INightCityAdminFacts _facts;

    public NightCityAstraServerHostConfiguration(INightCityAdminFacts facts)
    {
        _facts = facts;
    }

    public AstraServerHostOptions GetOptions()
    {
        if (!AstraConsumerLayout.TryLocate(out var root))
        {
            throw new InvalidOperationException(
                "AstraGraph consumer root was not found. Expected AstraGraph/Compatibility.json beside RobustToolbox.");
        }

        var robustRoot = Path.Combine(root, "RobustToolbox");
        if (!AstraConsumerLayout.TryReadGitCommit(robustRoot, out var commit))
        {
            throw new InvalidOperationException(
                $"AstraGraph could not read the RobustToolbox commit at '{robustRoot}'.");
        }

        return new AstraServerHostOptions
        {
            Storage = new StorageLayout(
                Path.Combine(root, AstraConsumerLayout.ProjectRelativePath),
                Path.Combine(root, AstraConsumerLayout.DataRelativePath)),
            CompatibilityManifestPath = Path.Combine(root, "AstraGraph", AstraConsumerLayout.ManifestFileName),
            RobustToolboxRoot = robustRoot,
            RobustCommit = commit,
            AdminDirectory = new FactsDirectory(_facts),
            EventAssemblies = ContentAssemblies()
        };
    }

    private static Assembly[] ContentAssemblies()
    {
        var found = new List<Assembly>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName().Name;
            if (name is "Content.Shared" or "Content.Server")
                found.Add(assembly);
        }

        return found.ToArray();
    }

    private sealed class FactsDirectory : IAstraAdminDirectory
    {
        private readonly INightCityAdminFacts _facts;

        public FactsDirectory(INightCityAdminFacts facts)
        {
            _facts = facts;
        }

        public bool TryGetAdmin(string userId, out uint adminFlags, out string? rank, out bool isSandbox)
        {
            return _facts.TryGetAdmin(userId, out adminFlags, out rank, out isSandbox);
        }
    }
}
