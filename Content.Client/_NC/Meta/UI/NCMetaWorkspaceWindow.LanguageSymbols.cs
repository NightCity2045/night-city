// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Content.Meta.Language;
using Content.Shared._NC.Meta.UI;

namespace Content.Client._NC.Meta.UI;

public sealed partial class NCMetaWorkspaceWindow
{
    private sealed class LanguageDownload(SemanticSnapshot snapshot, long generation, string stamp)
    {
        public readonly SemanticSnapshot Snapshot = snapshot;
        public readonly long Generation = generation;
        public readonly string Stamp = stamp;
        public readonly MemoryStream Bytes = new();
        public readonly TaskCompletionSource<MetaSemanticEnvironment?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid Transfer;
        public Guid Request;
        public string Hash = "";
        public int Total;
    }
    private LanguageDownload? _languageDownload;
    private long _languageProofUntil;

    private Task<MetaSemanticEnvironment?> ReadLanguageEnvironmentAsync(SemanticSnapshot snapshot, long generation, CancellationToken cancellation)
    {
        CancelLanguageDownload();
        var stamp = MetaSemanticHeaders.RevisionStamp(snapshot.Inventory.Select(file =>
            new MetaSemanticRevision(file.FileId.ToString("D"), file.Path, file.Revision, file.Sha256)));
        var download = new LanguageDownload(snapshot, generation, stamp);
        _languageDownload = download;
        RequestLanguagePage(download);
        return download.Completion.Task.WaitAsync(cancellation);
    }
    private void CancelLanguageDownload()
    {
        if (_languageDownload is not { } download) return;
        _languageDownload = null; download.Completion.TrySetResult(null); download.Bytes.Dispose();
    }
    private void RequestLanguagePage(LanguageDownload download)
    {
        download.Request = Guid.NewGuid();
        Requested?.Invoke(new(NCMetaWorkspaceOperation.ReadLanguageSymbols, download.Request,
            baseRevision: download.Snapshot.ProjectRevision, projectId: download.Snapshot.Project,
            utf8Offset: (int)download.Bytes.Length, chunkBytes: 32768, transferId: download.Transfer));
    }
    private void ApplyLanguageSymbols(NCMetaWorkspaceState state)
    {
        if (state.LanguageSymbols is not { } chunk) return;
        var download = _languageDownload;
        if (chunk.Revoked)
        {
            if (chunk.ProjectId == _state.ProjectId) InvalidateLanguageQuery();
            return;
        }
        if (download == null || state.AcknowledgedRequestId != download.Request) return;
        if (!IsSemanticSnapshotCurrent(download.Snapshot, download.Generation, requireProof: false) || chunk.ProjectId != download.Snapshot.Project ||
            chunk.ProjectRevision != download.Snapshot.ProjectRevision || chunk.RevisionStamp != download.Stamp ||
            chunk.TotalBytes is < 1 or > MetaSemanticHeaders.MaximumBytes || chunk.Json.Length > 32768 || Encoding.UTF8.GetByteCount(chunk.Json) > 32768 ||
            chunk.Utf8Offset != download.Bytes.Length || chunk.Sha256.Length != 64 || chunk.TransferId == Guid.Empty ||
            download.Transfer != Guid.Empty && (download.Transfer != chunk.TransferId || download.Hash != chunk.Sha256 || download.Total != chunk.TotalBytes))
        { InvalidateLanguageQuery(); return; }
        download.Transfer = chunk.TransferId; download.Hash = chunk.Sha256; download.Total = chunk.TotalBytes;
        var bytes = Encoding.UTF8.GetBytes(chunk.Json);
        if (download.Bytes.Length + bytes.Length > download.Total) { InvalidateLanguageQuery(); return; }
        download.Bytes.Write(bytes);
        if (!chunk.Complete)
        {
            if (download.Bytes.Length >= download.Total) { InvalidateLanguageQuery(); return; }
            RequestLanguagePage(download); return;
        }
        var body = download.Bytes.ToArray();
        if (body.Length != download.Total || Convert.ToHexStringLower(SHA256.HashData(body)) != download.Hash)
        { InvalidateLanguageQuery(); return; }
        try
        {
            var environment = JsonSerializer.Deserialize<MetaSemanticEnvironment>(body,
                new JsonSerializerOptions { MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
            if (environment == null || environment.RootModule is not { Length: >= 1 and <= 128 } ||
                environment.Package is not { Length: >= 1 and <= 128 } || environment.Version is not { Length: >= 1 and <= 128 } ||
                environment.Dependencies is not { Length: <= 16 })
            { InvalidateLanguageQuery(); return; }
            _languageDownload = null; download.Bytes.Dispose();
            _languageProofUntil = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
            download.Completion.TrySetResult(environment);
        }
        catch (JsonException) { InvalidateLanguageQuery(); }
    }
}
