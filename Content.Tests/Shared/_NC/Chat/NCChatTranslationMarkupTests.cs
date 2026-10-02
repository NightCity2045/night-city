// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Chat.Translation;
using NUnit.Framework;

namespace Content.Tests.Shared._NC.Chat;

[TestFixture]
public sealed class NCChatTranslationMarkupTests
{
    [TestCase("ru", "RU")]
    [TestCase("ru-RU", "RU")]
    [TestCase("EN", "EN")]
    [TestCase("en-US", "EN")]
    public void NormalizeLanguageCodeSupportsCultures(string input, string expected)
    {
        Assert.That(NCChatTranslationMarkup.NormalizeLanguageCode(input), Is.EqualTo(expected));
    }

    [Test]
    public void OriginalTextRoundTripsThroughTagEncoding()
    {
        const string original = "Привет, Night City!";
        var encoded = NCChatTranslationMarkup.EncodeOriginalText(original);

        Assert.That(NCChatTranslationMarkup.TryDecodeOriginalText(encoded, out var decoded), Is.True);
        Assert.That(decoded, Is.EqualTo(original));
    }
}
