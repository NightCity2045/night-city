// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using Content.Meta.Testing;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

/// <summary>The stock device gate written in META: it keeps a ledger and lets in everybody, or only the names on its list.</summary>
[TestFixture]
public sealed class MetaGateTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Resources", "Meta", "Gate"))) directory = directory.Parent;
            return directory!.FullName;
        }
    }

    private static VerifiedArtifact Compile()
    {
        var source = File.ReadAllText(Path.Combine(Root, "Resources", "Meta", "Gate", "main.meta"));
        var compiled = new MetaCompiler().Compile(new MetaCompilationRequest([new("main.meta", source)], "security.gate", "security.gate", "1.0.0"));
        Assert.That(compiled.Success, Is.True, string.Join(';', compiled.Diagnostics.Select(d => d.ToString())));
        return MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
    }

    private static void Run(string allowedList, string name, string expectedCommands)
    {
        var fixture = $$"""
            {
              "rootModule": "security.gate",
              "responses": [
                { "member": "meta.files.FileStore.readText", "value": "{{allowedList}}", "pending": true, "completeAfterSlices": 2 },
                { "member": "meta.files.FileStore.writeTextAtomic", "value": null, "pending": true, "completeAfterSlices": 2 },
                { "member": "meta.devices.Gate.answer", "value": null, "pending": false, "completeAfterSlices": 1 }
              ],
              "events": [
                { "slot": "gate", "event": "useRequested",
                  "arguments": [ { "requestId": "r1", "subject": { "name": "{{name}}", "key": "character:1" }, "stamp": { "epoch": 1, "sequence": 1, "tick": 60 } } ],
                  "sequence": 1, "nowMs": 1000, "tick": 60 }
              ],
              "expectedLogs": [],
              "expectedCommands": [ {{expectedCommands}} ],
              "epoch": 1, "nowMs": 1000, "tick": 60
            }
            """;
        var run = MetaTestHost.Run(Compile(), MetaTestHost.ParseFixture(fixture));
        Assert.That(run.Passed, Is.True, run.FailureCode + ":" + run.Fault + " cmds=" + string.Join(";", run.Commands.Select(c => c.Trace)));
    }

    private const string Read = "\"readText(gate/allowed.txt)\"";

    [Test]
    public void WithNoListEverybodyIsLetInAndWrittenDown() =>
        Run("", "Ann", Read + ", \"writeTextAtomic(gate/seen.txt,Ann\\n)\", \"answer(r1,true)\"");

    [Test]
    public void ANameOnTheListIsLetIn() =>
        Run("Ann\\nBob\\n", "Bob", Read + ", \"writeTextAtomic(gate/seen.txt,Bob\\n)\", \"answer(r1,true)\"");

    [Test]
    public void AnyoneNotOnTheListIsTurnedAwayButStillWrittenDown() =>
        Run("Ann\\n", "Eve", Read + ", \"writeTextAtomic(gate/seen.txt,Eve\\n)\", \"answer(r1,false)\"");
}
