// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using System.Linq;
using System.Text;
using Content.Client._NC.Meta.Editor;
using NUnit.Framework;

namespace Content.Tests.Client._NC.Meta;

[TestFixture]
public sealed class NCMetaGraphemeTest
{
    [TestCase("e\u0301日本🚀")]
    [TestCase("🇺🇦🇯🇵🇺")]
    [TestCase("👩🏽‍🚀👨‍👩‍👧‍👦")]
    [TestCase("\u1100\u1101\u1161\u11a8\u11a9\u0600A\r\nB")]
    [TestCase("क्‍ष\u0000\ud800a\udc00")]
    public void KeepsThePinnedFrameworksExtendedGraphemeBoundaries(string source) =>
        Assert.That(NCMetaGraphemes.Boundaries(source), Is.EqualTo(StringInfo.ParseCombiningCharacters(source)));

    [Test]
    public void EveryUnicodeScalarMatchesTheFrameworkWithCombiningAndEmojiContexts()
    {
        for (var scalar = 0; scalar <= 0x10ffff; scalar++)
        {
            if (!Rune.IsValid(scalar)) continue;
            var value = new Rune(scalar).ToString();
            foreach (var source in new[] { "A" + value + "B", value + "\u0301\u200d🚀" })
                if (!NCMetaGraphemes.Boundaries(source).SequenceEqual(StringInfo.ParseCombiningCharacters(source)))
                    Assert.Fail("Different grapheme boundaries at U+" + scalar.ToString("X"));
        }
    }
}
