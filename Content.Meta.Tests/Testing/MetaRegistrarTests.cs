// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Language;
using Content.Meta.Runtime;
using Content.Meta.Testing;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

/// <summary>The CitiNet name registrar written in META answers register and resolve messages.</summary>
[TestFixture]
public sealed class MetaRegistrarTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Resources", "Meta", "Registrar"))) directory = directory.Parent;
            return directory!.FullName;
        }
    }

    private static VerifiedArtifact Compile()
    {
        var compiler = new MetaCompiler();
        var source = File.ReadAllText(Path.Combine(Root, "Resources", "Meta", "Registrar", "main.meta"));
        var compiled = compiler.Compile(new MetaCompilationRequest([new("main.meta", source)], "citinet.registrar", "citinet.registrar", "1.0.0"));
        Assert.That(compiled.Success, Is.True, string.Join(';', compiled.Diagnostics.Select(d => d.ToString())));
        return MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
    }

    private static string Fixture(string topic, string payload, string expectedCommand, string expectedReply)
    {
        // The write is only answered when the test expects the registrar to save.
        var write = expectedCommand.Length == 0 ? "" : "{ \"member\": \"meta.files.FileStore.writeTextAtomic\", \"value\": null, \"pending\": true, \"completeAfterSlices\": 2 },";
        return $$"""
        {
          "rootModule": "citinet.registrar",
          "responses": [
            { "member": "meta.files.FileStore.readText", "value": "", "pending": true, "completeAfterSlices": 2 },
            {{write}}
            { "member": "meta.net.MessagePort.send", "value": { "messageId": "fixture-1", "accepted": true }, "pending": false, "completeAfterSlices": 1 }
          ],
          "events": [
            { "slot": "registrar-port", "event": "messageReceived",
              "arguments": [ { "replyTo": "peer-a", "topic": "{{topic}}", "payload": "{{payload}}", "stamp": { "epoch": 1, "sequence": 1, "tick": 60 } } ],
              "sequence": 1, "nowMs": 1000, "tick": 60 }
          ],
          "expectedLogs": [],
          "expectedCommands": [ "readText(registry/names.txt)"{{expectedCommand}}, "send(peer-a,{{expectedReply}})" ],
          "epoch": 1, "nowMs": 1000, "tick": 60
        }
        """;
    }

    private static void Run(string topic, string payload, string expectedCommand, string expectedReply)
    {
        var run = MetaTestHost.Run(Compile(), MetaTestHost.ParseFixture(Fixture(topic, payload, expectedCommand, expectedReply)));
        Assert.That(run.Passed, Is.True, run.FailureCode + ":" + run.Fault);
    }

    [Test]
    public void AFreeNameIsRegisteredSavedAndConfirmed() =>
        Run("dns.register", "demo|K7Q4MD|main", ", \"writeTextAtomic(registry/names.txt,demo|K7Q4MD|main|peer-a\\n)\"", "dns.registered,ok|demo");

    [Test]
    public void ANameWithCapitalsIsInvalid() =>
        Run("dns.register", "Demo|K7Q4MD|main", "", "dns.registered,invalid|Demo");

    [Test]
    public void ADeviceCodeOfTheWrongLengthIsInvalid() =>
        Run("dns.register", "demo|K7Q|main", "", "dns.registered,invalid|demo");

    [Test]
    public void AnUnknownNameResolvesToNothing() =>
        Run("dns.resolve", "ghost", "", "dns.answer,none|ghost");
}
