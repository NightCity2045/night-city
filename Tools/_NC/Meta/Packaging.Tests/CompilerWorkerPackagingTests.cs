// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Content.Packaging._NC.Meta;
using NUnit.Framework;
using Robust.Packaging;
using Robust.Packaging.AssetProcessing;
using Robust.Packaging.AssetProcessing.Passes;

namespace NightCity.Meta.Packaging.Tests;

[TestFixture]
public sealed class CompilerWorkerPackagingTests
{
    private string _root = "";
    private readonly Dictionary<string, byte[]> _worker = new(StringComparer.Ordinal)
    {
        ["Meta.CompilerWorker.dll"] = "compiled worker fixture"u8.ToArray(),
        ["Content.Meta.dll"] = "same pure core fixture"u8.ToArray(),
        ["Meta.CompilerWorker.deps.json"] = Encoding.UTF8.GetBytes("{\r\n  \"runtimeTarget\": {\"name\": \"net10.0\"}\r\n}"),
        ["Meta.CompilerWorker.runtimeconfig.json"] = Encoding.UTF8.GetBytes("{\r\n  \"runtimeOptions\": {\"tfm\": \"net10.0\"}\r\n}"),
    };
    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "nc-worker-packaging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "bin/Content.Server"));
        Directory.CreateDirectory(Path.Combine(_root, "bin/MetaCompilerWorker"));
        File.WriteAllBytes(Path.Combine(_root, "bin/Content.Server/Content.Meta.dll"), _worker["Content.Meta.dll"]);
        File.WriteAllBytes(Path.Combine(_root, "bin/Content.Server/Content.Server.dll"), "game module marker fixture"u8.ToArray());
        foreach (var (name, bytes) in _worker) File.WriteAllBytes(Path.Combine(_root, "bin/MetaCompilerWorker", name), bytes);
    }
    [TearDown]
    public void Cleanup()
    {
        var absolute = Path.GetFullPath(_root);
        Assert.That(Path.GetDirectoryName(absolute), Is.EqualTo(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)));
        Assert.That(Path.GetFileName(absolute), Does.StartWith("nc-worker-packaging-"));
        Directory.Delete(absolute, recursive: true);
    }
    private async Task<MemoryStream> Package()
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var graph = new RobustServerAssetGraph(parallel: false);
            var worker = NCMetaCompilerWorkerPackaging.CreateInput(graph);
            var writer = new AssetPassZipWriter(zip);
            writer.AddDependency(graph.Output);
            AssetGraph.CalculateGraph([writer, worker, ..graph.AllPasses]);
            NCMetaCompilerWorkerPackaging.InjectBundle(worker, _root);
            await RobustSharedPackaging.WriteContentAssemblies(graph.InputResources, _root, "Content.Server", ["Content.Server", "Content.Meta"]);
            graph.InputCore.InjectFile(new AssetFileMemory("Robust.Server.dll", "engine fixture"u8.ToArray()));
            graph.InputCore.InjectFinished(); graph.InputResources.InjectFinished(); worker.InjectFinished();
            await writer.FinishedTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        stream.Position = 0;
        using (var validation = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true)) NCMetaCompilerWorkerPackaging.ValidateArchive(validation);
        stream.Position = 0;
        return stream;
    }
    [Test]
    public async Task ActualServerGraphPublishesFourCanonicalFilesOutsideModuleTreeWithUnchangedBytes()
    {
        using var bytes = await Package(); using var zip = new ZipArchive(bytes, ZipArchiveMode.Read);
        Assert.That(zip.Entries.Select(entry => entry.FullName), Is.EquivalentTo(new[]
        {
            "Robust.Server.dll", "Resources/Assemblies/Content.Server.dll", "Resources/Assemblies/Content.Meta.dll",
            "MetaCompilerWorker/Meta.CompilerWorker.dll", "MetaCompilerWorker/Meta.CompilerWorker.deps.json",
            "MetaCompilerWorker/Meta.CompilerWorker.runtimeconfig.json", "MetaCompilerWorker/Content.Meta.dll",
        }));
        foreach (var (name, expected) in _worker)
        {
            using var source = zip.GetEntry("MetaCompilerWorker/" + name)!.Open(); using var actual = new MemoryStream(); source.CopyTo(actual);
            Assert.That(actual.ToArray(), Is.EqualTo(expected), "Worker metadata must bypass general CRLF normalization: " + name);
        }
        NCMetaCompilerWorkerPackaging.ValidateArchive(zip);
    }
    [TestCase("Meta.CompilerWorker.dll")]
    [TestCase("Meta.CompilerWorker.deps.json")]
    [TestCase("Meta.CompilerWorker.runtimeconfig.json")]
    [TestCase("Content.Meta.dll")]
    public void MissingAnyCanonicalFileFailsPackaging(string missing)
    {
        File.Delete(Path.Combine(_root, "bin/MetaCompilerWorker", missing));
        Assert.Throws<FileNotFoundException>(() => NCMetaCompilerWorkerPackaging.InjectBundle(new AssetPassPipe(), _root));
    }
    [Test]
    public void DifferentRuntimeCoreCannotBePublishedWithTheWorker()
    {
        File.WriteAllText(Path.Combine(_root, "bin/Content.Server/Content.Meta.dll"), "wrong core");
        Assert.Throws<InvalidDataException>(() => NCMetaCompilerWorkerPackaging.InjectBundle(new AssetPassPipe(), _root));
    }
    [Test]
    public void MalformedWorkerRuntimeMetadataFailsBeforePublishing()
    {
        File.WriteAllText(Path.Combine(_root, "bin/MetaCompilerWorker/Meta.CompilerWorker.runtimeconfig.json"), "{broken");
        Assert.That(() => NCMetaCompilerWorkerPackaging.InjectBundle(new AssetPassPipe(), _root), Throws.InstanceOf<JsonException>());
    }
    [TestCase("Resources/Assemblies/MetaCompilerWorker/Content.Meta.dll")]
    [TestCase("Resources/Assemblies/Meta.CompilerWorker.dll")]
    public async Task FinalZipRejectsRecursivelyDiscoveredWorkerAssemblies(string forbidden)
    {
        using var bytes = await Package();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Update, leaveOpen: true))
            using (var entry = zip.CreateEntry(forbidden).Open()) entry.Write("must never be loaded as a game module"u8);
        bytes.Position = 0; using var validation = new ZipArchive(bytes, ZipArchiveMode.Read);
        Assert.Throws<InvalidDataException>(() => NCMetaCompilerWorkerPackaging.ValidateArchive(validation));
    }
    [Test]
    public async Task FinalZipRejectsChangedRuntimeCoreAfterDeferredAssetReads()
    {
        using var bytes = await Package();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Update, leaveOpen: true))
        {
            zip.GetEntry("Resources/Assemblies/Content.Meta.dll")!.Delete();
            using var entry = zip.CreateEntry("Resources/Assemblies/Content.Meta.dll").Open(); entry.Write("changed during packaging"u8);
        }
        bytes.Position = 0; using var validation = new ZipArchive(bytes, ZipArchiveMode.Read);
        Assert.Throws<InvalidDataException>(() => NCMetaCompilerWorkerPackaging.ValidateArchive(validation));
    }
    [Test]
    public async Task FinalZipRejectsMissingBundleRatherThanShippingDisabledCompiler()
    {
        using var bytes = await Package();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Update, leaveOpen: true)) zip.GetEntry("MetaCompilerWorker/Meta.CompilerWorker.dll")!.Delete();
        bytes.Position = 0; using var validation = new ZipArchive(bytes, ZipArchiveMode.Read);
        Assert.Throws<InvalidDataException>(() => NCMetaCompilerWorkerPackaging.ValidateArchive(validation));
    }
}
