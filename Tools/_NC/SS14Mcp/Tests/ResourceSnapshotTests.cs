// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using NightCity.Diagnostics.Contracts;
using NightCity.Diagnostics.Core;
using NUnit.Framework;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;

namespace NightCity.Diagnostics.Tests;

[TestFixture]
public sealed class ResourceSnapshotTests
{
    [TestCase("SourceRoot", true, "invalid_path")]
    [TestCase("SourceAncestor", true, "invalid_path")]
    [TestCase("Nested", true, "invalid_path")]
    [TestCase("Nested", false, "invalid_resource_snapshot")]
    public async Task LinkedSourcePathsAndNestedDirectoriesAreRejectedBeforeAnyCopy(string position, bool outsideWorkspace, string code)
    {
        var root = Path.Combine(Path.GetTempPath(), "ss14-resource-links-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "ss14-resource-links-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var target = outsideWorkspace ? outside : Path.Combine(root, "target");
        Directory.CreateDirectory(target);
        var marker = Path.Combine(target, "marker.bin");
        await File.WriteAllBytesAsync(marker, [1, 2, 3]);
        var source = position == "SourceAncestor" ? "alias/Resources" : "Resources";
        var link = position switch
        {
            "SourceAncestor" => Path.Combine(root, "alias"),
            "Nested" => Path.Combine(root, "Resources", "linked"),
            _ => Path.Combine(root, "Resources"),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (position == "SourceAncestor") Directory.CreateDirectory(Path.Combine(target, "Resources"));
        try
        {
            await CreateDirectoryLink(link, target);
            var error = Assert.ThrowsAsync<DiagnosticException>(() =>
                IsolatedResourceSnapshot.CaptureAsync(new(root), source, "run", CancellationToken.None));
            Assert.Multiple(() =>
            {
                Assert.That(error!.Code, Is.EqualTo(code));
                Assert.That(Directory.Exists(Path.Combine(root, "run")), Is.False);
                Assert.That(File.ReadAllBytes(marker), Is.EqualTo(new byte[] { 1, 2, 3 }));
            });
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Assert.That(new DirectoryInfo(link).LinkTarget, Is.Not.Null);
                Directory.Delete(link);
            }
            DeleteOwnedDirectory(root);
            DeleteOwnedDirectory(outside);
        }
    }

    private static async Task CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        // Junctions exercise the real Windows reparse guard without requiring Developer Mode.
        // Encode literal owned paths so shell metacharacters in the user's temp path stay inert.
        var script = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + link.Replace("'", "''") +
            "' -Target '" + target.Replace("'", "''") + "' | Out-Null";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            Assert.Fail("Creating an owned test junction timed out.");
        }
        Assert.That(process.ExitCode, Is.Zero, await errors);
    }

    private static void DeleteOwnedDirectory(string directory)
    {
        var absolute = Path.GetFullPath(directory);
        Assert.That(Path.GetDirectoryName(absolute), Is.EqualTo(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)));
        Assert.That(Path.GetFileName(absolute), Does.StartWith("ss14-resource-links-"));
        Assert.That(new DirectoryInfo(absolute).Attributes & FileAttributes.ReparsePoint, Is.EqualTo((FileAttributes)0));
        Directory.Delete(absolute, recursive: true);
    }

    [Test]
    public async Task MultipleStreamedBatchesRetainTheCanonicalOrderedHashAndExactBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "ss14-resources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Resources"));
        try
        {
            var inventory = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            for (var index = 0; index < 137; index++)
            {
                var path = $"group-{index % 3}/{136 - index:D3}.bin";
                var bytes = new byte[index % 4 == 0 ? 65537 : index * 7];
                for (var offset = 0; offset < bytes.Length; offset++) bytes[offset] = (byte)(offset + index);
                inventory.Add(path, bytes);
                var file = Path.Combine(root, "Resources", path);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, bytes);
            }
            var canonical = string.Concat(inventory.Select(entry => entry.Key + "\0" +
                Convert.ToHexStringLower(SHA256.HashData(entry.Value)) + "\n"));
            var snapshot = await IsolatedResourceSnapshot.CaptureAsync(new(root), "Resources", "run", CancellationToken.None);
            Assert.That(snapshot.Sha256, Is.EqualTo(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))));
            Assert.That(snapshot.Files, Is.EqualTo(inventory.Count));
            Assert.That(snapshot.Bytes, Is.EqualTo(inventory.Sum(entry => (long)entry.Value.Length)));
            foreach (var entry in inventory)
                Assert.That(await File.ReadAllBytesAsync(Path.Combine(snapshot.Directory, entry.Key)), Is.EqualTo(entry.Value));
        }
        finally
        {
            Assert.That(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), Is.True);
            Directory.Delete(root, true);
        }
    }

    [Test]
    public async Task LaterPrototypeEditsCannotChangeAnExistingRunAndAreVisibleInTheNextRun()
    {
        var root = Path.Combine(Path.GetTempPath(), "ss14-resources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Resources", "Prototypes"));
        try
        {
            var paths = new WorkspacePaths(root);
            var source = Path.Combine(root, "Resources", "Prototypes", "test.yml");
            await File.WriteAllTextAsync(source, "first prototype");
            var first = await IsolatedResourceSnapshot.CaptureAsync(paths, "Resources", "run-one", CancellationToken.None);
            await File.WriteAllTextAsync(source, "second prototype");
            var second = await IsolatedResourceSnapshot.CaptureAsync(paths, "Resources", "run-two", CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(File.ReadAllText(Path.Combine(first.Directory, "Prototypes", "test.yml")), Is.EqualTo("first prototype"));
                Assert.That(File.ReadAllText(Path.Combine(second.Directory, "Prototypes", "test.yml")), Is.EqualTo("second prototype"));
                Assert.That(first.Sha256, Is.Not.EqualTo(second.Sha256));
                Assert.That(first.Files, Is.EqualTo(1));
            });
            Assert.ThrowsAsync<DiagnosticException>(async () =>
                await IsolatedResourceSnapshot.CaptureAsync(paths, "Resources", "run-one", CancellationToken.None));
        }
        finally
        {
            // Exact directory created above; no computed user/workspace target is removed.
            Assert.That(Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase), Is.True);
            Directory.Delete(root, true);
        }
    }
}
