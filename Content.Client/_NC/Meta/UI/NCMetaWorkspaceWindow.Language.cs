// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Meta.Language;
using Content.Shared._NC.Meta.UI;
using Robust.Shared.Asynchronous;

namespace Content.Client._NC.Meta.UI;

public sealed partial class NCMetaWorkspaceWindow
{
    [Dependency] private ITaskManager _languageTasks = default!;
    private CancellationTokenSource? _languageCancellation;
    private long _languageGeneration;
    private enum SemanticQuery : byte { Analyze, Complete, Definition, References }
    private sealed record SemanticFile(Guid Id, string Path, long Revision, string Source, string Hash);
    private sealed record SemanticSnapshot(Guid Project, long ProjectRevision, bool CanEdit, Guid Selected, int Caret, int Anchor,
        ImmutableArray<NCMetaFileSummary> Inventory, ImmutableArray<SemanticFile> Files);

    private void InvalidateLanguageQuery(bool clearResults = true)
    {
        _languageGeneration++;
        _languageCancellation?.Cancel(); _languageCancellation?.Dispose(); _languageCancellation = null;
        CancelLanguageDownload(); _languageProofUntil = 0;
        if (clearResults && !Disposed)
        { ClearLanguageResults(); SymbolDocs.Text = Loc.GetString("nc-meta-analysis-stale"); }
    }

    private bool SemanticStateChanged(NCMetaWorkspaceState state) => _state.ProjectId != state.ProjectId ||
        _state.ProjectRevision != state.ProjectRevision || _state.CanEdit != state.CanEdit ||
        (_state.NativeTools != null) != (state.NativeTools != null) || !_state.Files.SequenceEqual(state.Files);

    private SemanticSnapshot? CaptureSemanticSnapshot()
    {
        if (_nativeToolsMode || _state.NativeTools != null || _selectedFile == Guid.Empty) return null;
        var files = ImmutableArray.CreateBuilder<SemanticFile>();
        var bytes = 0;
        foreach (var file in _state.Files.Where(value => value.Path.EndsWith(".meta", StringComparison.Ordinal)))
        {
            if (!_buffers.TryGetValue(file.FileId, out var buffer)) continue; // No implicit read or closed-library/source fetch.
            var text = file.FileId == _selectedFile ? Editor.Source : buffer.Source;
            var size = Encoding.UTF8.GetByteCount(text);
            if (files.Count >= 64 || size > 256 * 1024 || (bytes += size) > 1024 * 1024) return null;
            var source = new SourceText(file.Path, text);
            files.Add(new(file.FileId, file.Path, buffer.Revision, source.Text, source.Revision));
        }
        if (!files.Any(value => value.Id == _selectedFile)) return null;
        return new(_state.ProjectId, _state.ProjectRevision, _state.CanEdit, _selectedFile, Editor.CaretUtf8, Editor.SelectionAnchorUtf16,
            _state.Files.ToImmutableArray(), files.ToImmutable());
    }

    private void TickSemanticProof()
    {
        if (_languageProofUntil != 0 && Stopwatch.GetTimestamp() >= _languageProofUntil) InvalidateLanguageQuery();
    }

    private bool IsSemanticSnapshotCurrent(SemanticSnapshot snapshot, long generation, bool requireProof = true) => !Disposed &&
        (!requireProof || Stopwatch.GetTimestamp() < _languageProofUntil) &&
        _languageGeneration == generation && !_nativeToolsMode && _state.NativeTools == null &&
        _state.ProjectId == snapshot.Project && _state.ProjectRevision == snapshot.ProjectRevision && _state.CanEdit == snapshot.CanEdit &&
        _state.Files.SequenceEqual(snapshot.Inventory) && snapshot.Files.All(file => _buffers.TryGetValue(file.Id, out var buffer) &&
            buffer.Revision == file.Revision && buffer.Source == file.Source);

    private async void RunSemanticQuery(SemanticQuery query)
    {
        InvalidateLanguageQuery(clearResults: false);
        ClearLanguageResults();
        var snapshot = CaptureSemanticSnapshot();
        if (snapshot == null) { SymbolDocs.Text = Loc.GetString("nc-meta-analysis-stale"); return; }
        var generation = _languageGeneration;
        var selected = snapshot.Files.Single(value => value.Id == snapshot.Selected);
        var prefix = query == SemanticQuery.Complete ? IdentifierAtCaret(true) : "";
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var token = cancellation.Token;
        _languageCancellation = cancellation;
        try
        {
            var environment = await ReadLanguageEnvironmentAsync(snapshot, generation, token);
            if (environment == null) return;
            var analysis = await Task.Run(() =>
            {
                using var compilerDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                compilerDeadline.CancelAfter(TimeSpan.FromMilliseconds(750));
                var request = new MetaCompilationRequest(snapshot.Files.Select(file => new MetaSourceFile(file.Path, file.Source)).ToArray(),
                    environment.RootModule, environment.Package, environment.Version, Library: environment.Library);
                return new MetaCompiler().AnalyzeSemantics(request, query == SemanticQuery.Complete
                    ? new(selected.Path, selected.Hash, snapshot.Caret) : null, compilerDeadline.Token, environment.Dependencies);
            }, token);
            _languageTasks.RunOnMainThread(() =>
            {
                if (analysis == null || !IsSemanticSnapshotCurrent(snapshot, generation) || _selectedFile != snapshot.Selected ||
                    Editor.Source != selected.Source || Editor.CaretUtf8 != snapshot.Caret || Editor.SelectionAnchorUtf16 != snapshot.Anchor) return;
                if (query == SemanticQuery.Analyze)
                {
                    foreach (var diagnostic in analysis.Diagnostics.Take(100))
                        if (snapshot.Files.FirstOrDefault(value => value.Path == diagnostic.FileId && value.Hash == diagnostic.SourceRevision) is { } file)
                            AddSemanticLocation(snapshot, generation, file, diagnostic.StartUtf8Byte,
                                Loc.GetString("nc-meta-local-diagnostic", ("code", diagnostic.Code), ("offset", diagnostic.StartUtf8Byte)));
                    foreach (var symbol in analysis.Index.Symbols.Where(value => value.Definition?.FileId == selected.Path &&
                                 value.Kind is not (MetaSemanticKind.Local or MetaSemanticKind.Parameter or MetaSemanticKind.Field or MetaSemanticKind.Variant)).Take(128 - _languageNavigation.Count))
                        AddSemanticLocation(snapshot, generation, selected, symbol.Definition!.Value.StartUtf8Byte, symbol.Signature);
                    SymbolDocs.Text = Loc.GetString("nc-meta-semantic-analysis");
                    return;
                }
                if (query == SemanticQuery.Complete)
                {
                    foreach (var candidate in analysis.Index.CompleteAt(selected.Path, selected.Hash, snapshot.Caret))
                    {
                        LanguageResults.AddItem(candidate.Signature);
                        _languageNavigation.Add(() =>
                        {
                            if (!IsSemanticSnapshotCurrent(snapshot, generation) || _selectedFile != snapshot.Selected ||
                                Editor.CaretUtf8 != snapshot.Caret || Editor.SelectionAnchorUtf16 != snapshot.Anchor ||
                                Editor.Source != selected.Source || IdentifierAtCaret(true) != prefix || Editor.CaretUtf16 != snapshot.Anchor)
                            { SymbolDocs.Text = Loc.GetString("nc-meta-analysis-stale"); return; }
                            if (candidate.Name.StartsWith(prefix, StringComparison.Ordinal)) Editor.TryReplaceSelection(candidate.Name[prefix.Length..]);
                        });
                    }
                    SymbolDocs.Text = Loc.GetString("nc-meta-semantic-completion");
                    return;
                }
                var bound = analysis.Index.SymbolAt(selected.Path, selected.Hash, snapshot.Caret);
                if (bound == null) { SymbolDocs.Text = Loc.GetString("nc-meta-analysis-stale"); return; }
                SymbolDocs.Text = bound.Signature; // Trusted language/schema metadata; never a world/device descriptor.
                var locations = query == SemanticQuery.References ? analysis.Index.ReferencesTo(bound.Id) :
                    bound.Definition is { } definition ? [definition] : ImmutableArray<SourceSpan>.Empty;
                foreach (var location in locations)
                    if (snapshot.Files.FirstOrDefault(value => value.Path == location.FileId && value.Hash == location.Revision) is { } file)
                        AddSemanticLocation(snapshot, generation, file, location.StartUtf8Byte,
                            Loc.GetString("nc-meta-symbol-location", ("path", file.Path), ("offset", location.StartUtf8Byte)));
            });
        }
        catch (Exception exception) when (exception is OperationCanceledException or ArgumentException or InvalidOperationException)
        {
            _languageTasks.RunOnMainThread(() =>
            {
                if (!Disposed && _languageGeneration == generation)
                { ClearLanguageResults(); SymbolDocs.Text = Loc.GetString("nc-meta-analysis-stale"); }
            });
        }
        finally
        {
            _languageTasks.RunOnMainThread(() =>
            {
                if (_languageCancellation == cancellation)
                { _languageCancellation = null; CancelLanguageDownload(); }
                cancellation.Dispose();
            });
        }
    }

    private void AddSemanticLocation(SemanticSnapshot snapshot, long generation, SemanticFile file, int offset, string label)
    {
        LanguageResults.AddItem(label);
        _languageNavigation.Add(() =>
        {
            if (!IsSemanticSnapshotCurrent(snapshot, generation))
            { SymbolDocs.Text = Loc.GetString("nc-meta-analysis-stale"); return; }
            SelectFile(file.Id); Editor.GoToUtf8(offset);
        });
    }
}
