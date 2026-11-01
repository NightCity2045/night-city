// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.IO.Compression;
using Content.Packaging;
using NUnit.Framework;
using Robust.Packaging;
using Robust.Packaging.AssetProcessing.Passes;

namespace NightCity.Meta.Packaging.Tests;

[TestFixture]
public sealed class ClientAssemblyPackagingTests
{
    [Test]
    public async Task ActualClientGraphExcludesTheServerCoreEvenWhenAStaleDllExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "nc-client-packaging-" + Guid.NewGuid().ToString("N"));
        var assemblies = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Content.Client"] = "client module fixture"u8.ToArray(),
            ["Content.Shared"] = "shared module fixture"u8.ToArray(),
            ["Content.Shared.Database"] = "database contracts fixture"u8.ToArray(),
            ["Content.Meta"] = "pure language and MUI core fixture"u8.ToArray(),
        };
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bin/Content.Client"));
            Directory.CreateDirectory(Path.Combine(root, "Resources"));
            foreach (var (name, bytes) in assemblies)
                File.WriteAllBytes(Path.Combine(root, "bin/Content.Client", name + ".dll"), bytes);

            using var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
            {
                var writer = new AssetPassZipWriter(zip);
                await ClientPackaging.WriteResources(root, writer, new PackageLoggerConsole(), default);
                await writer.FinishedTask.WaitAsync(TimeSpan.FromSeconds(5));
            }

            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            Assert.That(archive.Entries.Select(entry => entry.FullName),
                Is.EquivalentTo(assemblies.Keys.Where(name => name != "Content.Meta").Select(name => "Assemblies/" + name + ".dll")));
            foreach (var (name, expected) in assemblies.Where(pair => pair.Key != "Content.Meta"))
            {
                using var source = archive.GetEntry("Assemblies/" + name + ".dll")!.Open();
                using var actual = new MemoryStream();
                source.CopyTo(actual);
                Assert.That(actual.ToArray(), Is.EqualTo(expected), name);
            }
        }
        finally
        {
            var absolute = Path.GetFullPath(root);
            Assert.That(Path.GetDirectoryName(absolute),
                Is.EqualTo(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)));
            Assert.That(Path.GetFileName(absolute), Does.StartWith("nc-client-packaging-"));
            if (Directory.Exists(absolute))
                Directory.Delete(absolute, recursive: true);
        }
    }
}
