// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;
using Content.Meta.Testing;
using Content.Meta.Verification;
using NUnit.Framework;

namespace Content.Meta.Tests.Testing;

/// <summary>The CitiNet name registrar written in META: quotes, paid registrations and resolution over messages.</summary>
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

    private const string Reads =
        "{ \"member\": \"meta.files.FileStore.readText\", \"value\": \"\", \"pending\": true, \"completeAfterSlices\": 2 }," +
        "{ \"member\": \"meta.files.FileStore.readText\", \"value\": \"\", \"pending\": true, \"completeAfterSlices\": 2 },";
    private const string Write = "{ \"member\": \"meta.files.FileStore.writeTextAtomic\", \"value\": null, \"pending\": true, \"completeAfterSlices\": 2 },";
    private const string Send = "{ \"member\": \"meta.net.MessagePort.send\", \"value\": { \"messageId\": \"fixture-1\", \"accepted\": true }, \"pending\": false, \"completeAfterSlices\": 1 }";
    private static readonly string[] Starts = ["readText(registry/names.txt)", "readText(registry/receipts.txt)"];

    private static void Run(string topic, string payload, string responses, string[] commands)
    {
        var expected = string.Join(",", Starts.Concat(commands).Select(command => "\"" + command.Replace("\"", "\\\"") + "\""));
        var fixture = $$"""
            {
              "rootModule": "citinet.registrar",
              "responses": [ {{Reads}} {{responses}} {{Send}} ],
              "events": [
                { "slot": "registrar-port", "event": "messageReceived",
                  "arguments": [ { "replyTo": "peer-a", "topic": "{{topic}}", "payload": "{{payload}}", "stamp": { "epoch": 1, "sequence": 1, "tick": 60 } } ],
                  "sequence": 1, "nowMs": 1000, "tick": 60 }
              ],
              "expectedLogs": [],
              "expectedCommands": [ {{expected}} ],
              "epoch": 1, "nowMs": 1000, "tick": 60
            }
            """;
        var run = MetaTestHost.Run(Compile(), MetaTestHost.ParseFixture(fixture));
        Assert.That(run.Passed, Is.True, run.FailureCode + ":" + run.Fault);
    }

    private const string SelfDevice = "{ \"member\": \"meta.economy.Wallet.selfDevice\", \"value\": \"K7Q4MD\", \"pending\": false, \"completeAfterSlices\": 1 },";
    private const string Paid = "{ \"member\": \"meta.economy.Wallet.received\", \"value\": { \"id\": \"r1\", \"amount\": 100, \"memo\": \"citi:demo\" }, \"pending\": true, \"completeAfterSlices\": 2 },";

    [Test]
    public void AFreeNameIsQuotedWithItsPriceAndTheRegistrarsDevice() =>
        Run("dns.quote", "demo", SelfDevice, ["selfDevice()", "send(peer-a,dns.price,ok|demo|100|K7Q4MD)"]);

    [Test]
    public void ANewNameWithoutAReceiptIsNotRegistered() =>
        Run("dns.register", "demo|K7Q4MD|main|", "", ["send(peer-a,dns.registered,unpaid|demo)"]);

    [Test]
    public void ANewNameWithAMatchingReceiptIsRegisteredSavedAndConfirmed() =>
        Run("dns.register", "demo|K7Q4MD|main|r1", Paid + Write + Write,
            ["received(r1)", "writeTextAtomic(registry/names.txt,demo|K7Q4MD|main|peer-a\\n)", "writeTextAtomic(registry/receipts.txt,r1\\n)", "send(peer-a,dns.registered,ok|demo)"]);

    [Test]
    public void ANameWithCapitalsIsInvalid() =>
        Run("dns.register", "Demo|K7Q4MD|main|r1", "", ["send(peer-a,dns.registered,invalid|Demo)"]);

    [Test]
    public void AnEmptyRegistryListsNoSites() =>
        Run("dns.search", "", "", ["send(peer-a,dns.found,ok|)"]);

    [Test]
    public void AnUnknownNameResolvesToNothing() =>
        Run("dns.resolve", "ghost", "", ["send(peer-a,dns.answer,none|ghost)"]);
}
