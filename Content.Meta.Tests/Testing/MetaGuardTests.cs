// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using Content.Meta.Testing;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

/// <summary>The stock host guard written in META: it answers a sender only after that sender keeps hammering it.</summary>
[TestFixture]
public sealed class MetaGuardTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Resources", "Meta", "Guard"))) directory = directory.Parent;
            return directory!.FullName;
        }
    }

    private static VerifiedArtifact Compile()
    {
        var source = File.ReadAllText(Path.Combine(Root, "Resources", "Meta", "Guard", "main.meta"));
        var compiled = new MetaCompiler().Compile(new MetaCompilationRequest([new("main.meta", source)], "security.guard", "security.guard", "1.0.0"));
        Assert.That(compiled.Success, Is.True, string.Join(';', compiled.Diagnostics.Select(d => d.ToString())));
        return MetaVerifier.Verify(compiled.Artifact!, MetaApiCatalog.Default.CreateVerificationCatalog(compiled.Module!));
    }

    private const string Strike =
        "{ \"member\": \"meta.security.Countermeasure.surge\", \"value\": null, \"pending\": false, \"completeAfterSlices\": 1 }," +
        "{ \"member\": \"meta.security.Trace.locate\", \"value\": { \"device\": \"K7Q4MD\", \"place\": \"Docks / Pier 4\", \"owner\": \"character:7\" }, \"pending\": true, \"completeAfterSlices\": 2 }," +
        "{ \"member\": \"meta.files.FileStore.writeTextAtomic\", \"value\": null, \"pending\": true, \"completeAfterSlices\": 2 }";

    private static string Event(int sequence, string sender) =>
        $$"""{ "slot": "guard-port", "event": "messageReceived", "arguments": [ { "replyTo": "{{sender}}", "topic": "probe", "payload": "x", "stamp": { "epoch": 1, "sequence": {{sequence}}, "tick": 60 } } ], "sequence": {{sequence}}, "nowMs": 1000, "tick": 60 }""";

    private static void Run(string events, string commands, bool struck = false)
    {
        var fixture = $$"""
            {
              "rootModule": "security.guard",
              "responses": [ {{(struck ? Strike : "")}} ],
              "events": [ {{events}} ],
              "expectedLogs": [ {{(struck ? "\"surge|peer-a\", \"traced|K7Q4MD|Docks / Pier 4|character:7\"" : "")}} ],
              "expectedCommands": [ {{commands}} ],
              "epoch": 1, "nowMs": 1000, "tick": 60
            }
            """;
        var run = MetaTestHost.Run(Compile(), MetaTestHost.ParseFixture(fixture));
        Assert.That(run.Passed, Is.True, run.FailureCode + ":" + run.Fault + " logs=" + string.Join(";", run.Logs) + " cmds=" + string.Join(";", run.Commands.Select(c => c.ToString())));
    }

    [Test]
    public void ASenderThatMessagesOnceOrTwiceIsLeftAlone() => Run(Event(1, "peer-a") + "," + Event(2, "peer-a"), "");

    [Test]
    public void AThirdMessageFromTheSameSenderGetsASurge() =>
        Run(Event(1, "peer-a") + "," + Event(2, "peer-a") + "," + Event(3, "peer-a"), "\"surge(peer-a,2)\", \"locate(peer-a)\", \"writeTextAtomic(guard/ledger.txt,peer-a|3|Docks / Pier 4|character:7\\n)\"", struck: true);

    [Test]
    public void ThreeSendersMessagingOnceEachAreNotCountedAsOne() => Run(Event(1, "peer-a") + "," + Event(2, "peer-b") + "," + Event(3, "peer-c"), "");
}
