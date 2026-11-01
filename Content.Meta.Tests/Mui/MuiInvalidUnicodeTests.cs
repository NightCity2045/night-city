// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Collections.Immutable;
using Content.Meta.Mui;
using NUnit.Framework;

namespace Content.Meta.Tests.Mui;

[TestFixture]
public sealed class MuiInvalidUnicodeTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void LoneSurrogatesAreRejectedWithoutChangingTheSession(bool high)
    {
        // Construct at runtime: custom-attribute strings cannot preserve ill-formed UTF-16.
        var invalid = new string(high ? (char) 0xd800 : (char) 0xdc00, 1);
        var fields = new Dictionary<string, MuiField> { ["name"] = new(MuiValueKind.String) }.ToImmutableDictionary();
        var contract = new MuiAppContract("1.0", ImmutableDictionary<string, MuiViewContract>.Empty,
            ImmutableDictionary<string, MuiActionContract>.Empty, ImmutableHashSet<string>.Empty);
        Assert.That(MuiStyleSheet.TryParse("{\"version\":\"1.0\",\"rules\":[],\"tokens\":{\"x\":\"" + invalid + "\"}}", contract, out _, out _), Is.False);
        Assert.That(MuiDataValidator.TryValidate("{\"name\":\"" + invalid + "\"}", fields, 1024, out _), Is.False);
        using var session = new MuiSessionState(Guid.NewGuid(), fields);
        Assert.That(session.TryApplySnapshot(0, "{\"name\":\"original\"}"), Is.EqualTo(MuiUpdateResult.Applied));
        Assert.That(session.TryApplyPatch(0, 1, "{\"set\":{\"name\":\"" + invalid + "\"}}"), Is.EqualTo(MuiUpdateResult.Invalid));
        Assert.That(session.Revision, Is.Zero);
        Assert.That(session.Read("name")!.GetValue<string>(), Is.EqualTo("original"));
    }
}
