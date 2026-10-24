// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using Content.Meta.Language;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

/// <summary>Exercises the explicitly selected worker and rejects stale core copies before spawning it.</summary>
internal static class MetaTestWorkerBundle
{
    public static string Resolve()
    {
        var selected = Environment.GetEnvironmentVariable("NC_META_TEST_WORKER");
        if (string.IsNullOrEmpty(selected))
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Tools", "_NC", "Meta", "CompilerWorker")))
                directory = directory.Parent;
            Assert.That(directory, Is.Not.Null, "The worker repository root is unavailable.");
            selected = Path.Combine(directory!.FullName, "Tools", "_NC", "Meta", "CompilerWorker", "bin", "DebugOpt", "net10.0", "Meta.CompilerWorker.dll");
        }
        selected = Path.GetFullPath(selected);
        Assert.That(File.Exists(selected), Is.True, "Build the selected compiler worker before running its acceptance tests.");
        var workerCore = Path.Combine(Path.GetDirectoryName(selected)!, "Content.Meta.dll");
        Assert.That(File.Exists(workerCore), Is.True, "The selected worker bundle is incomplete.");
        Assert.That(SHA256.HashData(File.ReadAllBytes(workerCore)),
            Is.EqualTo(SHA256.HashData(File.ReadAllBytes(typeof(MetaCompiler).Assembly.Location))),
            "The worker and acceptance runtime contain different META cores.");
        return selected;
    }
}
