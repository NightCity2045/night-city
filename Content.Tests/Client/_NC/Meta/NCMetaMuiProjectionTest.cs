// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Text.Json;
using Content.Client._NC.Meta.Mui;
using Content.Shared._NC.Meta.UI;
using NUnit.Framework;

namespace Content.Tests.Client._NC.Meta;

[TestFixture]
public sealed class NCMetaMuiProjectionTest
{
    [Test]
    public void OutgoingFormsKeepUnicodeEscapesAndExactSignedIntegers()
    {
        var values = new NCMetaUiObject();
        values["message"] = NCMetaMuiValues.Scalar("\"Привет 🚀\"\n\\\u0000");
        values["integer"] = NCMetaMuiValues.Scalar(long.MaxValue);
        values["negative"] = NCMetaMuiValues.Scalar(long.MinValue);
        Assert.That(NCMetaMuiValues.TryWrite(values, 8192, out var json), Is.True);
        using var document = JsonDocument.Parse(json);
        Assert.That(document.RootElement.GetProperty("message").GetString(), Is.EqualTo("\"Привет 🚀\"\n\\\u0000"));
        Assert.That(document.RootElement.GetProperty("integer").GetInt64(), Is.EqualTo(long.MaxValue));
        Assert.That(document.RootElement.GetProperty("negative").GetInt64(), Is.EqualTo(long.MinValue));
    }

    [Test]
    public void MalformedUnicodeCyclesAndExcessivePayloadsAreRejected()
    {
        Assert.That(NCMetaMuiValues.TryWrite(NCMetaMuiValues.Scalar("\ud800"), 8192, out _), Is.False);
        Assert.That(NCMetaMuiValues.TryWrite(new NCMetaUiScalar { ValueKind = NCMetaUiValueKind.Number, Number = double.NaN }, 8192, out _), Is.False);
        var cycle = new NCMetaUiObject(); cycle["self"] = cycle;
        Assert.That(NCMetaMuiValues.TryWrite(cycle, 8192, out _), Is.False);
        var values = new NCMetaUiArray();
        for (var i = 0; i < 512; i++) values.Items.Add(NCMetaMuiValues.Scalar("🚀"));
        Assert.That(NCMetaMuiValues.TryWrite(values, 1024, out _), Is.False);
    }

    [Test]
    public void FormValidationPreservesRequiredOptionalAndNumericBounds()
    {
        var schema = new System.Collections.Generic.Dictionary<string, NCMetaMuiField>
        {
            ["count"] = new(NCMetaMuiValueKind.Integer, true, 4096, 1, 10),
            ["note"] = new(NCMetaMuiValueKind.String, false, 8, null, null),
        };
        var values = new NCMetaUiObject(); values["count"] = NCMetaMuiValues.Scalar(2);
        Assert.That(NCMetaMuiValues.Validate(values, schema, 8192), Is.True);
        values["note"] = new NCMetaUiScalar();
        Assert.That(NCMetaMuiValues.Validate(values, schema, 8192), Is.True);
        values["count"] = NCMetaMuiValues.Scalar(11);
        Assert.That(NCMetaMuiValues.Validate(values, schema, 8192), Is.False);
        values.Remove("count");
        Assert.That(NCMetaMuiValues.Validate(values, schema, 8192), Is.False);
    }
}
