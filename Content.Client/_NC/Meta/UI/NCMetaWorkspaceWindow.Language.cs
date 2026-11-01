// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using Content.Client._NC.Meta.Editor;
using Content.Client._NC.Meta.Mui;
using Content.Shared._NC.Meta.UI;
using Robust.Shared.Asynchronous;

namespace Content.Client._NC.Meta.UI;

public sealed partial class NCMetaWorkspaceWindow
{
    [Dependency] private ITaskManager _languageTasks = default!;
    private CancellationTokenSource? _languageCancellation;
    private long _languageGeneration;
    private enum SemanticQuery : byte { Analyze, Complete, Definition, References, AnalyzeMui, Preview, CompleteMui }
    private sealed record SemanticFile(Guid Id, string Path, long Revision, string Source, string Hash);
    private sealed record SemanticSnapshot(Guid Project, long ProjectRevision, bool CanEdit, Guid Selected, int Caret, int Anchor,
        ImmutableArray<NCMetaFileSummary> Inventory, ImmutableArray<SemanticFile> Files);
    private void InvalidateLanguageQuery(bool clearResults = true)
    {
        _languageGeneration++;
        _languageCancellation?.Cancel(); _languageCancellation?.Dispose(); _languageCancellation = null;
        CancelLanguageDownload(); _languageProofUntil = 0;
        if (clearResults && !Disposed) { ClearLanguageResults(); SymbolDocs.Text = Loc.GetString("nc-meta-analysis-stale"); }
    }
    private bool SemanticStateChanged(NCMetaWorkspaceState state) => _state.ProjectId != state.ProjectId ||
        _state.ProjectRevision != state.ProjectRevision || _state.CanEdit != state.CanEdit ||
        (_state.NativeTools != null) != (state.NativeTools != null) || !_state.Files.SequenceEqual(state.Files);
    private SemanticSnapshot? CaptureSemanticSnapshot(SemanticQuery query)
    {
        if (_nativeToolsMode || _state.NativeTools != null || _selectedFile == Guid.Empty) return null;
        var files = ImmutableArray.CreateBuilder<SemanticFile>(); var bytes = 0;
        foreach (var file in _state.Files.Where(value => query <= SemanticQuery.References
                     ? value.Path.EndsWith(".meta", StringComparison.Ordinal)
                     : value.Path.EndsWith(".mui", StringComparison.Ordinal) || value.Path.EndsWith(".mstyle", StringComparison.Ordinal)))
        {
            if (!_buffers.TryGetValue(file.FileId, out var buffer)) continue;
            var text = file.FileId == _selectedFile ? Editor.Source : buffer.Source;
            var size = Encoding.UTF8.GetByteCount(text);
            if (files.Count >= 64 || size > 256 * 1024 || (bytes += size) > 1024 * 1024) return null;
            files.Add(new(file.FileId, file.Path, buffer.Revision, text, NCMetaSourceHash.HashText(text)));
        }
        if (!files.Any(value => value.Id == _selectedFile)) return null;
        if (query == SemanticQuery.Preview)
        {
            var size = Encoding.UTF8.GetByteCount(MockState.Text);
            if (size > NCMetaMuiValues.InitialBytes || bytes + size > 1024 * 1024) return null;
            files.Add(new(Guid.Empty, "@preview", 0, MockState.Text, NCMetaSourceHash.HashText(MockState.Text)));
        }
        return new(_state.ProjectId, _state.ProjectRevision, _state.CanEdit, _selectedFile, Editor.CaretUtf8, Editor.SelectionAnchorUtf16,
            _state.Files.ToImmutableArray(), files.ToImmutable());
    }
    private void TickSemanticProof()
    {
        if (_languageProofUntil != 0 && Stopwatch.GetTimestamp() >= _languageProofUntil) InvalidateLanguageQuery();
    }
    private bool IsSemanticSnapshotCurrent(SemanticSnapshot snapshot, long generation, bool requireProof = true) => !Disposed &&
        (!requireProof || Stopwatch.GetTimestamp() < _languageProofUntil) && _languageGeneration == generation && !_nativeToolsMode &&
        _state.NativeTools == null && _state.ProjectId == snapshot.Project && _state.ProjectRevision == snapshot.ProjectRevision &&
        _state.CanEdit == snapshot.CanEdit && _state.Files.SequenceEqual(snapshot.Inventory) &&
        snapshot.Files.All(file => file.Id == Guid.Empty ? MockState.Text == file.Source :
            _buffers.TryGetValue(file.Id, out var buffer) && buffer.Revision == file.Revision && buffer.Source == file.Source);
    private async void RunSemanticQuery(SemanticQuery query)
    {
        InvalidateLanguageQuery(clearResults: false); ClearLanguageResults();
        var snapshot = CaptureSemanticSnapshot(query);
        if (snapshot == null) { SymbolDocs.Text = Loc.GetString("nc-meta-analysis-stale"); return; }
        var generation = _languageGeneration;
        var selected = snapshot.Files.Single(value => value.Id == snapshot.Selected);
        var prefix = query is SemanticQuery.Complete or SemanticQuery.CompleteMui ? IdentifierAtCaret(true) : "";
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = cancellation.Token; _languageCancellation = cancellation;
        try
        {
            var reply = await ReadLanguageEnvironmentAsync(snapshot, generation, query, token);
            _languageTasks.RunOnMainThread(() =>
            {
            if (reply == null || reply.Rejected || !IsSemanticSnapshotCurrent(snapshot, generation) || _selectedFile != snapshot.Selected ||
                Editor.Source != selected.Source || Editor.CaretUtf8 != snapshot.Caret || Editor.SelectionAnchorUtf16 != snapshot.Anchor) return;
            if (query == SemanticQuery.Preview)
            {
                if (reply.Presentation == null || !NCMetaMuiValues.PresentationValid(reply.Presentation, reply.ViewId)) return;
                NCMetaUiLifecycle.RemoveChildren(PreviewHost);
                _preview = new(reply.Presentation, reply.ViewId, Guid.NewGuid());
                PreviewHost.AddChild(_preview);
                _preview.ActionRequested += (action, body) => PreviewAction.Text = Loc.GetString("nc-meta-preview-action-body", ("action", action), ("body", body));
                var result = _preview.TryUpdate(0, 0, reply.Values, true);
                PreviewCosts.Text = Loc.GetString("nc-meta-preview-costs", ("nodes", reply.Presentation.NodeCount), ("controls", _preview.NativeControlCount),
                    ("bytes", Encoding.UTF8.GetByteCount(MockState.Text)), ("rules", reply.Presentation.Styles.Count));
                Status.Text = Loc.GetString(result == NCMetaMuiUpdateResult.Applied ? "nc-meta-preview-ready" : "nc-meta-preview-invalid", ("code", "MUI1101"));
                return;
            }
            if (query == SemanticQuery.AnalyzeMui) { SymbolDocs.Text = Loc.GetString("nc-meta-preview-ready"); return; }
            foreach (var diagnostic in reply.Diagnostics.Take(100))
                if (snapshot.Files.FirstOrDefault(file => file.Id == diagnostic.FileId && file.Revision == diagnostic.Revision) is { } file)
                    AddSemanticLocation(snapshot, generation, file, diagnostic.StartUtf8Byte,
                        Loc.GetString("nc-meta-local-diagnostic", ("code", diagnostic.Code), ("offset", diagnostic.StartUtf8Byte)));
            foreach (var item in reply.Items.Take(Math.Max(0, 128 - _languageNavigation.Count)))
            {
                if (query is SemanticQuery.Complete or SemanticQuery.CompleteMui)
                {
                    LanguageResults.AddItem(item.Signature);
                    _languageNavigation.Add(() =>
                    {
                        if (!IsSemanticSnapshotCurrent(snapshot, generation) || _selectedFile != snapshot.Selected || Editor.CaretUtf8 != snapshot.Caret ||
                            Editor.SelectionAnchorUtf16 != snapshot.Anchor || Editor.Source != selected.Source ||
                            IdentifierAtCaret(true) != prefix || Editor.CaretUtf16 != snapshot.Anchor)
                        { SymbolDocs.Text = Loc.GetString("nc-meta-analysis-stale"); return; }
                        if (item.Name.StartsWith(prefix, StringComparison.Ordinal)) Editor.TryReplaceSelection(item.Name[prefix.Length..]);
                    });
                }
                else if (snapshot.Files.FirstOrDefault(file => file.Path == item.Path && file.Hash == item.Hash) is { } file)
                    AddSemanticLocation(snapshot, generation, file, item.Offset, query == SemanticQuery.Analyze ? item.Signature :
                        Loc.GetString("nc-meta-symbol-location", ("path", file.Path), ("offset", item.Offset)));
            }
            SymbolDocs.Text = query is SemanticQuery.Complete or SemanticQuery.CompleteMui ? Loc.GetString("nc-meta-semantic-completion") :
                query == SemanticQuery.Analyze ? Loc.GetString("nc-meta-semantic-analysis") : reply.Signature;
            });
        }
        catch (Exception exception) when (exception is OperationCanceledException or ArgumentException or InvalidOperationException)
        {
            _languageTasks.RunOnMainThread(() =>
            {
                if (!Disposed && _languageGeneration == generation) { ClearLanguageResults(); SymbolDocs.Text = Loc.GetString("nc-meta-analysis-stale"); }
            });
        }
        finally
        {
            _languageTasks.RunOnMainThread(() =>
            {
                if (_languageCancellation == cancellation) { _languageCancellation = null; CancelLanguageDownload(); }
                cancellation.Dispose();
            });
        }
    }
    private void AddSemanticLocation(SemanticSnapshot snapshot, long generation, SemanticFile file, int offset, string label)
    {
        LanguageResults.AddItem(label);
        _languageNavigation.Add(() =>
        {
            if (!IsSemanticSnapshotCurrent(snapshot, generation)) { SymbolDocs.Text = Loc.GetString("nc-meta-analysis-stale"); return; }
            SelectFile(file.Id); Editor.GoToUtf8(offset);
        });
    }
}
