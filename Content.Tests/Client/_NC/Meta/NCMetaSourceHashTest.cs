// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Security.Cryptography;
using System.Text;
using Content.Client._NC.Meta.Editor;
using NUnit.Framework;

namespace Content.Tests.Client._NC.Meta;

[TestFixture]
public sealed class NCMetaSourceHashTest
{
    [TestCase("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
    [TestCase("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
    public void UsesTheStandardSha256ContentDigest(string text, string expected) =>
        Assert.That(NCMetaSourceHash.HashText(text), Is.EqualTo(expected));

    [TestCase(1)] [TestCase(55)] [TestCase(56)] [TestCase(63)] [TestCase(64)] [TestCase(65)]
    [TestCase(127)] [TestCase(128)] [TestCase(1024)] [TestCase(262144)] [TestCase(1048576)]
    public void MatchesTheServerProviderAcrossPaddingAndSourceSizeBoundaries(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte) (i * 73 + i / 251);
        var expected = SHA256.HashData(bytes);
        Assert.That(NCMetaSourceHash.HashData(bytes), Is.EqualTo(expected));
        Assert.That(NCMetaSourceHash.ToHexString(expected), Is.EqualTo(Convert.ToHexString(expected)));
        Assert.That(NCMetaSourceHash.ToHexStringLower(expected), Is.EqualTo(Convert.ToHexString(expected).ToLowerInvariant()));
    }

    [Test]
    public void UnicodeSourceRetainsItsUtf8Digest()
    {
        const string source = "Ночной город 🚀 e\u0301\n日本";
        Assert.That(NCMetaSourceHash.HashText(source),
            Is.EqualTo(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant()));
    }

    [Test]
    public void Utf8TransferDecodingRejectsInvalidSequencesInsteadOfReplacingThem()
    {
        const string source = "Кириллица 🚀 日本";
        Assert.That(NCMetaSourceHash.DecodeUtf8(Encoding.UTF8.GetBytes(source)), Is.EqualTo(source));
        foreach (var bytes in new[] { new byte[] { 0xc0, 0xaf }, new byte[] { 0xed, 0xa0, 0x80 }, new byte[] { 0xf0, 0x9f, 0x9a } })
            Assert.Throws<ArgumentException>(() => NCMetaSourceHash.DecodeUtf8(bytes));
    }
}
