// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using System.Text;
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

public sealed record ResourceSnapshotInfo(string Directory, string Sha256, int Files, long Bytes);

/// <summary>Copies resource bytes once so an isolated playtest cannot hot-reload another work session's prototypes.</summary>
public static class IsolatedResourceSnapshot
{
    public static async Task<ResourceSnapshotInfo> CaptureAsync(WorkspacePaths paths, string source, string destination,
        CancellationToken cancellationToken)
    {
        source = paths.Resolve(source);
        destination = paths.Resolve(destination);
        if (!System.IO.Directory.Exists(source) || System.IO.Directory.Exists(destination))
            throw new DiagnosticException("invalid_resource_snapshot", "A resource snapshot needs an existing source and a fresh destination.");
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(source);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                paths.Resolve(entry.FullName);
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new DiagnosticException("invalid_resource_snapshot", "Resource snapshots cannot contain links.");
                if (entry is DirectoryInfo) pending.Push(entry.FullName);
                else if (files.Count < 300000) files.Add(entry.FullName);
                else throw new DiagnosticException("resource_snapshot_limit", "The resource snapshot file limit was exceeded.");
            }
        }
        System.IO.Directory.CreateDirectory(destination);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var ordered = files.Order(StringComparer.Ordinal).ToArray();
        long total = 0;
        // A fixed-size batch preserves the exact ordered inventory hash while limiting open
        // streams, buffers and queued work. Large files are streamed instead of retained.
        for (var start = 0; start < ordered.Length; start += 64)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(64, ordered.Length - start);
            var entries = new (string Source, string Relative, long Length)[count];
            var hashes = new string[count];
            for (var index = 0; index < count; index++)
            {
                var file = ordered[start + index];
                var length = new FileInfo(file).Length;
                if (length > 256 * 1024 * 1024 || total > 16L * 1024 * 1024 * 1024 - length)
                    throw new DiagnosticException("resource_snapshot_limit", "The resource snapshot byte limit was exceeded.");
                entries[index] = (file, Path.GetRelativePath(source, file), length);
                total += length;
            }
            await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions
            {
                CancellationToken = cancellationToken, MaxDegreeOfParallelism = 8,
            }, async (index, token) =>
            {
                var entry = entries[index];
                var target = Path.Combine(destination, entry.Relative);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (input.Length != entry.Length) throw Changed();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var fileDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                long copied = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) != 0)
                {
                    copied += read;
                    if (copied > entry.Length) throw Changed();
                    fileDigest.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
                if (copied != entry.Length) throw Changed();
                hashes[index] = Convert.ToHexStringLower(fileDigest.GetHashAndReset());
            });
            for (var index = 0; index < count; index++)
                digest.AppendData(Encoding.UTF8.GetBytes(entries[index].Relative.Replace('\\', '/') + "\0" + hashes[index] + "\n"));
        }
        return new(destination, Convert.ToHexStringLower(digest.GetHashAndReset()), files.Count, total);
    }

    private static DiagnosticException Changed() => new("resource_snapshot_changed", "A resource changed while its snapshot was being captured.");
}
