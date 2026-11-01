// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Diagnostics;
using NightCity.Diagnostics.Contracts;
using NightCity.Diagnostics.Core;
using NightCity.Diagnostics.Seeds;
using NUnit.Framework;

namespace NightCity.Diagnostics.Tests;

[TestFixture]
public sealed class SqliteSeedPrefillTests
{
    [Test]
    public void OptInRequiresAllFourPinsAndNeverAcceptsPathsAsIdentity()
    {
        var values = new Dictionary<string, string>();
        Assert.That(SqliteSeedPrefill.FromVariables(key => values.GetValueOrDefault(key)), Is.Null);
        values[SqliteSeedPrefill.IdVariable] = Guid.NewGuid().ToString("N");
        Assert.Throws<DiagnosticException>(() => SqliteSeedPrefill.FromVariables(key => values.GetValueOrDefault(key)));
        values[SqliteSeedPrefill.SeedVariable] = new('a', 64);
        values[SqliteSeedPrefill.ManifestVariable] = new('b', 64);
        Assert.Throws<DiagnosticException>(() => SqliteSeedPrefill.FromVariables(key => values.GetValueOrDefault(key)));
        values[SqliteSeedPrefill.HelperVariable] = new('c', 64);
        Assert.That(SqliteSeedPrefill.FromVariables(key => values.GetValueOrDefault(key))!.SeedId, Is.EqualTo(values[SqliteSeedPrefill.IdVariable]));
        values[SqliteSeedPrefill.IdVariable] = "../another/helper.dll";
        Assert.Throws<DiagnosticException>(() => SqliteSeedPrefill.FromVariables(key => values.GetValueOrDefault(key)));
        values[SqliteSeedPrefill.IdVariable] = Guid.Empty.ToString("N");
        Assert.Throws<DiagnosticException>(() => SqliteSeedPrefill.FromVariables(key => values.GetValueOrDefault(key)));
    }
    [Test]
    public void GameAndHelperChildrenNeverInheritMemoryBootstrapOrMcpSecrets()
    {
        var start = new ProcessStartInfo("dotnet"); start.Environment.Clear();
        string[] stripped = [SqliteSeedPrefill.IdVariable, SqliteSeedPrefill.SeedVariable, SqliteSeedPrefill.ManifestVariable,
            SqliteSeedPrefill.HelperVariable, "NC_DIAGNOSTICS_WORKSPACE", "NC_DIAGNOSTICS_SQLITE_SEED_ID", "NC_DIAGNOSTICS_SQLITE_SEED_SHA256",
            "NC_DIAGNOSTICS_SQLITE_MANIFEST_SHA256", "ROBUST_AUTH_TOKEN", "OPENAI_API_KEY", "ANTHROPIC_API_KEY"];
        foreach (var key in stripped) start.Environment[key] = "must not reach a child";
        start.Environment["NC_DIAGNOSTICS_ISOLATED"] = "1"; start.Environment["PATH"] = "normal-runtime-search";
        SqliteSeedPrefill.ClearBootstrapEnvironment(start);
        Assert.That(stripped.Any(start.Environment.ContainsKey), Is.False);
        Assert.That(start.Environment["NC_DIAGNOSTICS_ISOLATED"], Is.EqualTo("1"));
        Assert.That(start.Environment["PATH"], Is.EqualTo("normal-runtime-search"));
    }
    [TestCase("../outside.dll")]
    [TestCase("/absolute.dll")]
    [TestCase("runtimes/win-x64/../../injection.dll")]
    [TestCase("tool.exe")]
    [TestCase("runtimes/win-x64/native/subdirectory/injection.dll")]
    public void HelperInventoryDoesNotAllowExecutablesOrPathTraversal(string name) => Assert.That(SqliteSeedArtifact.IsEntryName(name), Is.False);
}
