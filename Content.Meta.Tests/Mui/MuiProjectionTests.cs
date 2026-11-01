// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text;
using System.Text.Json;
using Content.Meta.Language;
using Content.Meta.Mui;
using Content.Meta.Runtime;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Mui;

[TestFixture]
public sealed class MuiProjectionTests
{
    [TestCase(2, true)]
    [TestCase(6, false)]
    public void UnicodeProjectionUsesBoundedUtf8Output(int rowCount, bool accepted)
    {
        const string source = """
            module projection.test;
            pub struct State { rows:array<string>; }
            fn main()->void {}
            """;
        var compiled = new MetaCompiler().Compile(new([new("src/main.meta", source)], "projection.test"));
        Assert.That(compiled.Success, Is.True, string.Join(',', compiled.Diagnostics.Select(x => x.Code)));
        var artifact = MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
        var stateType = Enumerable.Range(0, artifact.TypeCount).Single(x => artifact.GetType(x).Name == "projection.test.State");
        var arrayType = artifact.GetType(stateType).Fields[0].TypeId;
        var text = string.Concat(Enumerable.Repeat("Я🙂e\u0301", 512));
        Assert.That(text.Length, Is.LessThanOrEqualTo(MuiLimits.StringLength));
        var expectedJson = JsonSerializer.Serialize(new { rows = Enumerable.Repeat(text, rowCount).ToArray() });
        Assert.That(Encoding.UTF8.GetByteCount(expectedJson) <= MuiLimits.InitialStateBytes, Is.EqualTo(accepted));

        var process = new MetaProcess(artifact);
        var value = process.CreateString(text);
        var rows = process.CreateArray(arrayType, Enumerable.Repeat(value, rowCount).ToArray());
        var state = process.CreateRecord(stateType, [rows]);
        var schema = new Dictionary<string, MuiField> { ["rows"] = new(MuiValueKind.Array, MaxLength: MuiLimits.ChartPoints) };

        Assert.That(MuiDtoCodec.TryProject(process, artifact, stateType, state, schema, out var json), Is.EqualTo(accepted));
        if (!accepted)
        {
            Assert.That(json, Is.Empty);
            return;
        }
        Assert.That(Encoding.UTF8.GetByteCount(json), Is.LessThanOrEqualTo(MuiLimits.InitialStateBytes));
        using var document = JsonDocument.Parse(json);
        var actualRows = document.RootElement.GetProperty("rows");
        Assert.That(actualRows.GetArrayLength(), Is.EqualTo(rowCount));
        foreach (var row in actualRows.EnumerateArray())
            Assert.That(row.GetString(), Is.EqualTo(text));
    }
}
