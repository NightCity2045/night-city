// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using NightCity.Diagnostics.Contracts;
using NightCity.Diagnostics.Core;
using NUnit.Framework;

namespace NightCity.Diagnostics.Tests;

[TestFixture]
public sealed class DiagnosticRuntimeBuildTests
{
    [Test]
    public void TheCommonBuildIsSelectedAndExplicitBundlesAreRejected()
    {
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "runtime-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new WorkspacePaths(root);
            var selected = DiagnosticRuntimeBuild.Resolve(paths, null);
            Assert.That(selected.BuildId, Is.Null);
            Assert.That(selected.ServerAssembly, Does.EndWith(Path.Combine("Content.Server", "Content.Server.dll")));
            Assert.Throws<DiagnosticException>(() => DiagnosticRuntimeBuild.Resolve(paths, Guid.NewGuid().ToString("N")));
            Assert.Throws<DiagnosticException>(() => DiagnosticRuntimeBuild.Resolve(paths, "../outside"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
