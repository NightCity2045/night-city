// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NightCity.Diagnostics.Contracts;
using NightCity.Diagnostics.Core;
using NUnit.Framework;

namespace NightCity.Diagnostics.Tests;

[TestFixture]
public sealed class DiagnosticRuntimeBuildTests
{
    [TestCase(1)]
    [TestCase(2)]
    public void AnExplicitBuildUsesItsVerifiedBinariesAndPrebuildResourcesAndRejectsLaterTampering(int version)
    {
        var temporaryParent = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(temporaryParent, "meta-runtime-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new WorkspacePaths(root);
            var id = Guid.NewGuid().ToString("N");
            var cohort = paths.Resolve("artifacts/meta-acceptance/" + id);
            var tests = Path.Combine(cohort, "bin/Content.IntegrationTests");
            string[] names = ["Content.Server.dll", "Content.Client.dll", "Content.Shared.dll", "Content.Meta.dll",
                "Content.Server.Database.dll", "Content.Shared.Database.dll", "Content.Tests.dll", "Content.IntegrationTests.dll",
                "MetaCompilerWorker/Meta.CompilerWorker.dll", "MetaCompilerWorker/Meta.CompilerWorker.deps.json",
                "MetaCompilerWorker/Meta.CompilerWorker.runtimeconfig.json", "MetaCompilerWorker/Content.Meta.dll"];
            var inventory = new List<object>();
            foreach (var name in names)
            {
                var bytes = Encoding.UTF8.GetBytes("fixture inventory " + (name == "MetaCompilerWorker/Content.Meta.dll" ? "Content.Meta.dll" : name));
                var inventoryRoot = version == 2 && name.StartsWith("MetaCompilerWorker/", StringComparison.Ordinal) ? Path.Combine(cohort, "bin") : tests;
                var file = Path.Combine(inventoryRoot, name);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, bytes);
                inventory.Add(new { name, sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes = bytes.Length });
            }
            void Copy(string project, string name, string? source = null)
            {
                var target = Path.Combine(cohort, "bin", project, name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(tests, source ?? name), target);
            }
            foreach (var name in new[] { "Content.Server.dll", "Content.Shared.dll", "Content.Meta.dll", "Content.Server.Database.dll", "Content.Shared.Database.dll" }) Copy("Content.Server", name);
            foreach (var name in new[] { "Content.Client.dll", "Content.Shared.dll", "Content.Meta.dll", "Content.Shared.Database.dll" }) Copy("Content.Client", name);
            if (version == 1)
                foreach (var name in names.Where(value => value.StartsWith("MetaCompilerWorker/", StringComparison.Ordinal))) Copy("Content.Server", name);
            var resources = Path.Combine(cohort, "Resources");
            Directory.CreateDirectory(resources);
            File.WriteAllText(Path.Combine(cohort, "build-manifest.json"), JsonSerializer.Serialize(new
            {
                version, buildId = id, testDirectory = tests, assemblies = inventory,
                resources = new { buildId = id, directory = resources, capturedBeforeBuild = true },
            }));
            var selected = DiagnosticRuntimeBuild.Resolve(paths, id);
            Assert.That(selected.BuildId, Is.EqualTo(id));
            Assert.That(selected.Resources, Is.EqualTo(resources));
            File.WriteAllText(selected.ClientAssembly, "changed after acceptance");
            Assert.Throws<DiagnosticException>(() => DiagnosticRuntimeBuild.Resolve(paths, id));
            Assert.Throws<DiagnosticException>(() => DiagnosticRuntimeBuild.Resolve(paths, "../outside"));
        }
        finally
        {
            var absolute = Path.GetFullPath(root);
            Assert.That(Path.GetDirectoryName(absolute), Is.EqualTo(temporaryParent.TrimEnd(Path.DirectorySeparatorChar)));
            Directory.Delete(absolute, true);
        }
    }
}
