using System.Diagnostics;
using System.IO.Compression;
using Robust.Packaging;
using Robust.Packaging.AssetProcessing;
using Robust.Packaging.AssetProcessing.Passes;
using Robust.Packaging.Utility;
using Robust.Shared.Timing;

namespace Content.Packaging;

public static class ClientPackaging
{
    // NC-Edit-Start
    private static readonly List<string> ClientNotExtraAssemblies = new()
    {
        "JetBrains.Annotations",
    };
    // NC-Edit-End

    /// <summary>
    /// Be advised this can be called from server packaging during a HybridACZ build.
    /// </summary>
    public static async Task PackageClient(bool skipBuild, bool logBuild, string configuration, IPackageLogger logger)
    {
        logger.Info("Building client...");

        if (!skipBuild)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList =
                {
                    "build",
                    Path.Combine("Content.Client", "Content.Client.csproj"),
                    "-c", configuration,
                    "--nologo",
                    "/v:m",
                    "/t:Rebuild",
                    "/p:FullRelease=true",
                    "/m"
                }
            };

            if (logBuild)
            {
                startInfo.ArgumentList.Add($"/bl:{Path.Combine("release", "client.binlog")}");
                startInfo.ArgumentList.Add("/p:ReportAnalyzer=true");
            }

            await ProcessHelpers.RunCheck(startInfo);
        }

        logger.Info("Packaging client...");

        var sw = RStopwatch.StartNew();
        {
            await using var zipFile =
                File.Open(Path.Combine("release", "SS14.Client.zip"), FileMode.Create, FileAccess.ReadWrite);
            using var zip = new ZipArchive(zipFile, ZipArchiveMode.Update);
            var writer = new AssetPassZipWriter(zip);

            await WriteResources("", writer, logger, default);
            await writer.FinishedTask;
        }

        logger.Info($"Finished packaging client in {sw.Elapsed}");
    }

    public static async Task WriteResources(
        string contentDir,
        AssetPass pass,
        IPackageLogger logger,
        CancellationToken cancel)
    {
        var graph = new RobustClientAssetGraph();
        pass.Dependencies.Add(new AssetPassDependency(graph.Output.Name));

        var dropSvgPass = new AssetPassFilterDrop(f => f.Path.EndsWith(".svg"))
        {
            Name = "DropSvgPass",
        };
        dropSvgPass.AddDependency(graph.Input).AddBefore(graph.PresetPasses);

        AssetGraph.CalculateGraph([pass, dropSvgPass, ..graph.AllPasses], logger);

        var inputPass = graph.Input;

        // NC-Edit-Start
        // AstraGraph lives in non-Content assemblies, so package every client dependency that the launcher does not provide.
        var sourcePath = Path.Combine(contentDir, "bin", "Content.Client");
        var deps = DepsHandler.Load(Path.Combine(sourcePath, "Content.Client.deps.json"));
        var contentAssemblies = GetContentAssemblyNamesToCopy(deps);

        await RobustSharedPackaging.WriteContentAssemblies(
            inputPass,
            contentDir,
            "Content.Client",
            contentAssemblies,
            cancel: cancel);
        // NC-Edit-End

        await RobustClientPackaging.WriteClientResources(
            contentDir,
            inputPass,
            SharedPackaging.AdditionalIgnoredResources,
            cancel);

        inputPass.InjectFinished();
    }

    // NC-Edit-Start
    private static IEnumerable<string> GetContentAssemblyNamesToCopy(DepsHandler deps)
    {
        var depsContent = deps.RecursiveGetLibrariesFrom("Content.Client").SelectMany(GetLibraryNames);
        var depsRobust = deps.RecursiveGetLibrariesFrom("Robust.Client");

        // WebView is supplied as a launcher engine module rather than as content.
        if (deps.Libraries.ContainsKey("Robust.Client.WebView"))
            depsRobust.UnionWith(deps.RecursiveGetLibrariesFrom("Robust.Client.WebView"));

        var robustAssemblies = depsRobust.SelectMany(GetLibraryNames).ToHashSet();
        return depsContent
            .Except(robustAssemblies)
            .Select(path => path[..^4])
            .Where(path => !ClientNotExtraAssemblies.Any(path.StartsWith))
            .Order(StringComparer.Ordinal);

        IEnumerable<string> GetLibraryNames(string library) => deps.Libraries[library].GetDllNames();
    }
    // NC-Edit-End
}
