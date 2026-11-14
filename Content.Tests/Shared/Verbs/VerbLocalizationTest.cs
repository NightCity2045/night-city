using System.Collections.Generic;
using Content.Shared.Verbs;
using NUnit.Framework;

namespace Content.Tests.Shared.Verbs;

[TestFixture]
public sealed class VerbLocalizationTest
{
    [TestCase("verb-common-open", "Открыть", "Open")]
    [TestCase("verb-follow-text", "Следовать", "Follow")]
    [TestCase("toggle-lock-verb-lock", "Заблокировать", "Lock")]
    public void TranslatedCopiesMergeAndResolveForExecution(string key, string clientText, string serverText)
    {
        var clientVerb = new AlternativeVerb { Text = clientText, TextLocalizationId = key };
        var serverVerb = new AlternativeVerb { Text = serverText, TextLocalizationId = key };
        var menu = new SortedSet<Verb> { clientVerb };
        menu.UnionWith(new[] { serverVerb });

        Assert.That(menu, Has.Count.EqualTo(1));
        var serverVerbs = new SortedSet<Verb> { serverVerb };
        Assert.That(serverVerbs.TryGetValue(clientVerb, out var resolved), Is.True);
        Assert.That(resolved, Is.SameAs(serverVerb));
    }

    [Test]
    public void DifferentActionsWithTheSameLabelRemainDistinct()
    {
        var verbs = new SortedSet<Verb>
        {
            new Verb { Text = "Open", TextLocalizationId = "verb-common-open" },
            new Verb { Text = "Open", TextLocalizationId = "comp-storage-verb-open-storage" },
        };
        Assert.That(verbs, Has.Count.EqualTo(2));
    }

    [Test]
    public void LegacyDynamicLabelsRemainDistinct()
    {
        var verbs = new SortedSet<Verb>
        {
            new Verb { Text = "Transfer 5" },
            new Verb { Text = "Transfer 10" },
            new Verb { Text = "Transfer 5" },
        };
        Assert.That(verbs, Has.Count.EqualTo(2));
    }
}
