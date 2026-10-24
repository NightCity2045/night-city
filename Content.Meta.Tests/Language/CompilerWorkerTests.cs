// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using Content.Meta.Tests.Testing;
using Content.Server._NC.Meta.Compilation;
using NUnit.Framework;

namespace Content.Meta.Tests.Language;

[TestFixture]
public sealed class CompilerWorkerTests
{
    private static string WorkerPath()
    {
        return MetaTestWorkerBundle.Resolve();
    }
    [TestCase("Content.Server")]
    [TestCase("Content.IntegrationTests")]
    public void PublishedBundleStaysOutsideTheGameAssemblyTree(string project)
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "meta-worker-layout", "bin"));
        var scanned = Path.Combine(parent, project) + Path.DirectorySeparatorChar;
        var resolved = MetaCompilerWorkerPool.ResolvePublishedWorkerPath(scanned);
#if FULL_RELEASE
        Assert.That(resolved, Is.EqualTo(Path.Combine(scanned, "MetaCompilerWorker", "Meta.CompilerWorker.dll")));
        Assert.That(resolved.StartsWith(Path.Combine(scanned, "Resources", "Assemblies") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), Is.False);
#else
        Assert.That(resolved, Is.EqualTo(Path.Combine(parent, "MetaCompilerWorker", "Meta.CompilerWorker.dll")));
        Assert.That(resolved.StartsWith(scanned, StringComparison.OrdinalIgnoreCase), Is.False);
#endif
        Assert.Throws<ArgumentException>(() => MetaCompilerWorkerPool.ResolvePublishedWorkerPath("relative"));
    }
    [Test]
    public void CanonicalLayoutDoesNotSearchForAnAlternateWorkerOrDependOnRuntimeMarkers()
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "worker-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var assemblyDirectory = Path.Combine(root, "Resources", "Assemblies");
            Directory.CreateDirectory(assemblyDirectory);
            File.WriteAllBytes(Path.Combine(assemblyDirectory, "Content.Server.dll"), []);
#if FULL_RELEASE
            var expected = Path.Combine(root, "MetaCompilerWorker", "Meta.CompilerWorker.dll");
#else
            var expected = Path.GetFullPath(Path.Combine(root, "..", "MetaCompilerWorker", "Meta.CompilerWorker.dll"));
#endif
            Assert.That(MetaCompilerWorkerPool.ResolvePublishedWorkerPath(root), Is.EqualTo(expected));
            Assert.That(File.Exists(expected), Is.False, "A missing canonical bundle must not select an alternate worker.");
            File.WriteAllBytes(Path.Combine(root, "Meta.CompilerWorker.dll"), []);
            Assert.That(MetaCompilerWorkerPool.ResolvePublishedWorkerPath(root), Is.EqualTo(expected));
        }
        finally
        {
            File.Delete(Path.Combine(root, "Resources", "Assemblies", "Content.Server.dll"));
            File.Delete(Path.Combine(root, "Meta.CompilerWorker.dll"));
            Directory.Delete(Path.Combine(root, "Resources", "Assemblies"));
            Directory.Delete(Path.Combine(root, "Resources"));
            Directory.Delete(root);
        }
    }
    [Test]
    public async Task BoundedWorkerRoundTripReverifiesArtifactAndDiagnosesSource()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Production worker resource policy is currently Windows Job Objects.");
        await using var pool = new MetaCompilerWorkerPool(WorkerPath());
        var valid = new MetaCompilationRequest([new("hello.meta", "module test.hello; import meta.system; fn main()->void { system.log(\"hello\"); }")], "test.hello");
        var compiled = await pool.CompileAsync("worker-test", valid);
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(d => d.Code)));
        var invalid = new MetaCompilationRequest([new("bad.meta", "module test.bad; var number:float=42;")], "test.bad");
        var diagnosed = await pool.CompileAsync("worker-test", invalid);
        Assert.That(diagnosed.Success, Is.False);
        Assert.That(diagnosed.Diagnostics.Select(d => d.Code), Does.Contain("E201"));
    }
    [Test]
    public async Task OneOutstandingRequestPerPrincipalAndCancellation()
    {
        if (!OperatingSystem.IsWindows()) Assert.Ignore("Production worker resource policy is currently Windows Job Objects.");
        await using var pool = new MetaCompilerWorkerPool(WorkerPath());
        var request = new MetaCompilationRequest([new("test.meta", "module test.program; fn main()->void {}")], "test.program");
        var first = pool.CompileAsync("same-principal", request);
        Assert.ThrowsAsync<InvalidOperationException>(async () => await pool.CompileAsync("same-principal", request));
        Assert.That((await first).Success, Is.True);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await pool.CompileAsync("canceled", request, canceled.Token));
    }
}
