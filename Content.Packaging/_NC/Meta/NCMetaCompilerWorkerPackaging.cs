// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Robust.Packaging;
using Robust.Packaging.AssetProcessing;
using Robust.Packaging.AssetProcessing.Passes;

namespace Content.Packaging._NC.Meta;

/// <summary>The isolated compiler is executable support data, outside the game's recursively scanned assemblies.</summary>
public static class NCMetaCompilerWorkerPackaging
{
    private const string DirectoryName = "MetaCompilerWorker";
    private const string RuntimeCore = "Resources/Assemblies/Content.Meta.dll";
    private const int MaximumFileBytes = 32 * 1024 * 1024;
    private static readonly string[] Files =
    ["Meta.CompilerWorker.dll", "Meta.CompilerWorker.deps.json", "Meta.CompilerWorker.runtimeconfig.json", "Content.Meta.dll"];

    public static AssetPassPipe CreateInput(RobustServerAssetGraph graph)
    {
        var input = new AssetPassPipe { Name = "NCMetaCompilerWorkerInput" };
        // Keep canonical dependency/configuration bytes outside general text normalization.
        graph.Output.AddDependency(input);
        return input;
    }

    public static void InjectBundle(AssetPass input, string contentDirectory, CancellationToken cancel = default)
    {
        var source = Path.GetFullPath(Path.Combine(contentDirectory, "bin", DirectoryName));
        var runtime = Path.GetFullPath(Path.Combine(contentDirectory, "bin", "Content.Server", "Content.Meta.dll"));
        var files = new List<(string Name, byte[] Bytes)>();
        // Validate the complete bundle before injecting any of it into an asynchronous graph.
        foreach (var name in Files)
        {
            cancel.ThrowIfCancellationRequested();
            var bytes = ReadBounded(Path.Combine(source, name));
            if (name.EndsWith(".json", StringComparison.Ordinal))
            {
                using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid META compiler worker metadata.");
            }
            files.Add((name, bytes));
        }
        if (!SHA256.HashData(ReadBounded(runtime)).AsSpan().SequenceEqual(SHA256.HashData(files.Single(file => file.Name == "Content.Meta.dll").Bytes)))
            throw new InvalidDataException("META compiler worker and published server must use the same Content.Meta.dll.");
        foreach (var file in files)
        {
            cancel.ThrowIfCancellationRequested();
            input.InjectFile(new AssetFileMemory(DirectoryName + "/" + file.Name, file.Bytes));
        }
    }

    /// <summary>Checks final ZIP bytes as well, after all deferred disk reads and asset passes have completed.</summary>
    public static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Mode != ZipArchiveMode.Read) throw new ArgumentException("Reopen the completed server ZIP for read-only validation.", nameof(archive));
        var entries = archive.Entries.Where(entry => entry.FullName.StartsWith(DirectoryName + "/", StringComparison.Ordinal)).ToArray();
        if (entries.Length != Files.Length || entries.Any(entry => !Files.Any(name => entry.FullName == DirectoryName + "/" + name)) ||
            entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != Files.Length)
            throw new InvalidDataException("Published META compiler worker inventory is incomplete or duplicated.");
        if (archive.Entries.Count(entry => entry.FullName == RuntimeCore) != 1 ||
            archive.Entries.Any(entry => entry.FullName.StartsWith("Resources/Assemblies/", StringComparison.Ordinal) &&
                (entry.FullName.Contains("/" + DirectoryName + "/", StringComparison.Ordinal) ||
                 entry.FullName.EndsWith("/Meta.CompilerWorker.dll", StringComparison.Ordinal) ||
                 entry.FullName.EndsWith("/Content.Meta.dll", StringComparison.Ordinal) && entry.FullName != RuntimeCore)))
            throw new InvalidDataException("META worker assemblies cannot be published inside the game module tree.");
        var runtime = archive.GetEntry(RuntimeCore)!;
        var worker = archive.GetEntry(DirectoryName + "/Content.Meta.dll")!;
        using var runtimeStream = OpenBounded(runtime); using var workerStream = OpenBounded(worker);
        if (!SHA256.HashData(runtimeStream).AsSpan().SequenceEqual(SHA256.HashData(workerStream)))
            throw new InvalidDataException("The final ZIP contains different META runtime and compiler cores.");
        foreach (var entry in entries) using (OpenBounded(entry)) { }
    }

    private static Stream OpenBounded(ZipArchiveEntry entry)
    {
        if (entry.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException("META worker archive size limit.");
        return entry.Open();
    }
    private static byte[] ReadBounded(string path)
    {
        for (var cursor = path; !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor))
        {
            var item = new FileInfo(cursor);
            if (item.LinkTarget != null || (File.Exists(cursor) || System.IO.Directory.Exists(cursor)) &&
                (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("META worker packaging sources cannot contain links.");
        }
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 or > MaximumFileBytes) throw new InvalidDataException("META worker source size limit.");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("META worker source changed during packaging.");
        return bytes;
    }
}
