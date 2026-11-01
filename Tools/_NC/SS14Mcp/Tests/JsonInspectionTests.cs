// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json.Nodes;
using NightCity.Diagnostics.Core;
using NUnit.Framework;

namespace NightCity.Diagnostics.Tests;

[TestFixture]
public sealed class JsonInspectionTests
{
    [Test]
    public void MissingFieldsAreNotEqualNullObservations()
    {
        var data = JsonNode.Parse("{\"present\":null,\"array\":[1,2],\"escaped/name\":true}");
        Assert.Multiple(() =>
        {
            Assert.That(JsonInspection.TryAt(data, "/present", out var value), Is.True);
            Assert.That(value, Is.Null);
            Assert.That(JsonInspection.TryAt(data, "/missing", out _), Is.False);
            Assert.That(JsonInspection.At(data, "/array/1")!.GetValue<int>(), Is.EqualTo(2));
            Assert.That(JsonInspection.At(data, "/escaped~1name")!.GetValue<bool>(), Is.True);
            Assert.That(JsonInspection.Diff(data, data, ["/missing"])["conclusive"]!.GetValue<bool>(), Is.False);
        });
    }

    [Test]
    public void ExplicitNumericToleranceAllowsQuantizedValues()
    {
        Assert.Multiple(() =>
        {
            Assert.That(JsonInspection.Equal(JsonNode.Parse("1.01"), JsonNode.Parse("1.02"), 0.02), Is.True);
            Assert.That(JsonInspection.Equal(JsonNode.Parse("1.01"), JsonNode.Parse("1.02")), Is.False);
        });
    }
}
