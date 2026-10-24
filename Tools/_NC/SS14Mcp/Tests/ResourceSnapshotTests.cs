// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using NightCity.Diagnostics.Contracts;
using NightCity.Diagnostics.Core;
using NUnit.Framework;
using System.Security.Cryptography;
using System.Text;

namespace NightCity.Diagnostics.Tests;

[TestFixture]
public sealed class ResourceSnapshotTests
{
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
