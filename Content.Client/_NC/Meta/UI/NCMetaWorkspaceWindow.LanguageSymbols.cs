// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Shared._NC.Meta.UI;

namespace Content.Client._NC.Meta.UI;

public sealed partial class NCMetaWorkspaceWindow
{
    public event Action<BoundUserInterfaceMessage>? AuthoringRequested;
    private sealed class LanguageDownload(SemanticSnapshot snapshot, long generation)
    {
        public readonly SemanticSnapshot Snapshot = snapshot;
        public readonly long Generation = generation;
        public readonly Guid Id = Guid.NewGuid();
        public readonly byte[][] Bytes = snapshot.Files.Select(file => Encoding.UTF8.GetBytes(file.Source)).ToArray();
        public readonly HashSet<(int File, int Offset)> Pending = [];
        public readonly TaskCompletionSource<NCMetaAuthoringReplyMessage?> Completion = new();
        public int File, Offset;
        public bool Ready, Running, Complete;
    }
    private LanguageDownload? _languageDownload;
    private Guid _languageProofId;
    private long _languageProofUntil;
    private Task<NCMetaAuthoringReplyMessage?> ReadLanguageEnvironmentAsync(SemanticSnapshot snapshot, long generation,
        SemanticQuery query, CancellationToken cancellation)
    {
        CancelLanguageDownload();
        var download = new LanguageDownload(snapshot, generation); _languageDownload = download;
        AuthoringRequested?.Invoke(new NCMetaAuthoringBeginMessage(download.Id, snapshot.Project, snapshot.ProjectRevision,
            snapshot.Selected, (int) query, snapshot.Caret, snapshot.Files.Select((file, index) =>
                new NCMetaDraftFile(file.Id, file.Path, file.Revision, download.Bytes[index].Length, file.Hash)).ToList(), snapshot.Inventory.ToList()));
        return download.Completion.Task.WaitAsync(cancellation);
    }
    private void CancelLanguageDownload()
    {
        if (_languageDownload is not { } download) return;
        _languageDownload = null;
        if (!download.Complete) AuthoringRequested?.Invoke(new NCMetaAuthoringRunMessage(download.Id, cancel: true));
        download.Completion.TrySetResult(null);
    }
    private void ApplyLanguageSymbols(NCMetaWorkspaceState state)
    {
        if (state.LanguageSymbols is { Revoked: true } chunk && chunk.ProjectId == _state.ProjectId) InvalidateLanguageQuery();
    }
    public void ApplyAuthoringReply(NCMetaAuthoringReplyMessage reply)
    {
        if (reply.Rejected && reply.Id == _languageProofId) { InvalidateLanguageQuery(); return; }
        if (_languageDownload is not { } download || reply.Id != download.Id) return;
        if (!IsSemanticSnapshotCurrent(download.Snapshot, download.Generation, requireProof: false) || reply.Rejected)
        {
            download.Complete = true; download.Completion.TrySetResult(null);
            SymbolDocs.Text = Loc.GetString("nc-meta-preview-invalid", ("code", reply.Code)); return;
        }
        if (reply.Complete)
        {
            if (reply.Items is not { Count: <= 128 } || reply.Diagnostics is not { Count: <= 100 } || reply.Signature.Length > 4096 ||
                reply.Items.Any(item => item == null || item.Name.Length > 4096 || item.Signature.Length > 4096 || item.Path.Length > 192 || item.Hash.Length > 64))
            { InvalidateLanguageQuery(); return; }
            download.Complete = true; _languageProofId = download.Id;
            _languageProofUntil = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
            download.Completion.TrySetResult(reply); return;
        }
        if (reply.Ready && !download.Ready) download.Ready = true;
        else if (!download.Pending.Remove((reply.File, reply.Offset))) return;
        PumpLanguageUpload(download);
    }
    private void PumpLanguageUpload(LanguageDownload download)
    {
        if (!download.Ready || download.Running || download.Complete) return;
        while (download.Pending.Count < 4 && download.File < download.Bytes.Length)
        {
            var bytes = download.Bytes[download.File];
            if (download.Offset == bytes.Length) { download.File++; download.Offset = 0; continue; }
            var count = Math.Min(32768, bytes.Length - download.Offset); var part = new byte[count];
            Array.Copy(bytes, download.Offset, part, 0, count);
            var offset = download.Offset; download.Offset += count;
            download.Pending.Add((download.File, download.Offset));
            AuthoringRequested?.Invoke(new NCMetaAuthoringChunkMessage(download.Id, download.File, offset, part));
        }
        if (download.File == download.Bytes.Length && download.Pending.Count == 0)
        { download.Running = true; AuthoringRequested?.Invoke(new NCMetaAuthoringRunMessage(download.Id)); }
    }
}
