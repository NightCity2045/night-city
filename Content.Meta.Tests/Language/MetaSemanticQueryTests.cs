// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using NUnit.Framework;

namespace Content.Meta.Tests.Language;

[TestFixture]
public sealed class MetaSemanticQueryTests
{
    private const string Text = "module draft; fn answer()->int {return 42;} fn main()->void {let number:int=answer();}";
    private static readonly MetaCompilationRequest Request = new([new("unsaved.meta", Text)], "draft");

    [Test]
    public void UnsavedBuffersProduceBoundDefinitionsAndReferencesWithoutArtifacts()
    {
        var source = new SourceText("unsaved.meta", Text);
        var offset = Text.LastIndexOf("answer()", StringComparison.Ordinal) + 1;
        var query = new MetaSemanticQuery(2, source.FileId, source.Revision, offset, []);
        var definition = MetaSemanticQueryRunner.Run(Request, query, CancellationToken.None);
        Assert.That(definition.Items, Has.Length.EqualTo(1));
        Assert.That(definition.Items[0].Location!.Value.Revision, Is.EqualTo(source.Revision));
        Assert.That(definition.Items[0].Location!.Value.StartUtf8Byte, Is.EqualTo(Text.IndexOf("answer", StringComparison.Ordinal)));
        var references = MetaSemanticQueryRunner.Run(Request, query with { Kind = 3 }, CancellationToken.None);
        Assert.That(references.Items, Has.Length.EqualTo(1));
        Assert.That(references.Items[0].Location!.Value.StartUtf8Byte, Is.EqualTo(offset - 1));
    }

    [Test]
    public void AChangedDraftOrCanceledQueryCannotReturnOldNavigation()
    {
        var query = new MetaSemanticQuery(0, "unsaved.meta", new string('0', 64), 0, []);
        Assert.Throws<ArgumentException>(() => MetaSemanticQueryRunner.Run(Request, query, CancellationToken.None));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => MetaSemanticQueryRunner.Run(Request,
            query with { Revision = new SourceText("unsaved.meta", Text).Revision }, cancellation.Token));
    }
}
