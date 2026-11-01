// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;
using NightCity.Diagnostics.Core;
using NUnit.Framework;

namespace NightCity.Diagnostics.Tests;

[TestFixture]
public sealed class ScenarioTests
{
    [Test]
    public void MissingReferenceCannotPassAsNullAndReferencesPreserveTypes()
    {
        var context = JsonNode.Parse("""{"steps":{"a":{"number":3,"empty":null}}}""")!.AsObject();
        Assert.That(ScenarioRunner.Resolve(JsonNode.Parse("""{"$ref":"/steps/a/number"}"""), context, 1024)!.GetValue<int>(), Is.EqualTo(3));
        Assert.That(ScenarioRunner.Resolve(JsonNode.Parse("""{"$ref":"/steps/a/empty"}"""), context, 1024), Is.Null);
        Assert.Throws<DiagnosticException>(() => ScenarioRunner.Resolve(JsonNode.Parse("""{"$ref":"/steps/a/missing"}"""), context, 1024));
    }

    [Test]
    public void UnsafeCommandsAndDuplicateStepsAreRejectedBeforeExecution()
    {
        Assert.Throws<DiagnosticException>(() => ScenarioRunner.Validate(JsonNode.Parse("""{"id":"bad","steps":[{"id":"a","command":"console_execute"}]}""")!.AsObject()));
        Assert.Throws<DiagnosticException>(() => ScenarioRunner.Validate(JsonNode.Parse("""{"id":"bad","steps":[{"id":"a","assert":{"actual":1,"equals":1}},{"id":"a","assert":{"actual":1,"equals":1}}]}""")!.AsObject()));
    }

    [Test]
    public async Task FailureRetainsReportAndCleansFixtureThroughTheBridge()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ss14-scenario-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var bridge = new ScenarioBridge();
            using var application = new DiagnosticApplication(directory, bridge);
            application.AddTarget(new DiagnosticTarget("test-server", "server", "http://127.0.0.1:1212/", new string('x', 32)));
            var document = JsonNode.Parse("""{"id":"failure-test","targetId":"test-server","steps":[{"id":"fixture","command":"fixture_create"},{"id":"bad","assert":{"actual":"<script>alert(1)</script>","equals":"different"}}]}""")!.AsObject();
            var failure = Assert.ThrowsAsync<DiagnosticOperationFailedException>(async () => await application.Scenarios.RunAsync(document, CancellationToken.None));
            Assert.That(failure!.Fault.Code, Is.EqualTo("assertion_failed"));
            Assert.That(failure.Result!["report"]!["steps"]![1]!["assertion"]!["actual"]!.GetValue<string>(), Does.Contain("<script>"));
            Assert.That(bridge.Commands, Does.Contain("fixture_destroy").And.Contain("lease_release"));
            Assert.That(application.Scenarios.Running, Is.False);
            var report = application.Artifacts.List().Single(item => item.MediaType == "text/html");
            var html = await File.ReadAllTextAsync(report.Path);
            Assert.That(html, Does.Not.Contain("<script>"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestCase("{\"token\":\"private-value\"}")]
    [TestCase("Authorization: Bearer private-value")]
    [TestCase("api_key='private-value'")]
    [TestCase("NC_DIAGNOSTICS_TOKEN=private-value")]
    public void KnownSecretFormsAreRedacted(string text)
    {
        var buffer = new DiagnosticLogBuffer();
        buffer.Add("Info", "test", text);
        Assert.That(buffer.Read(0).Entries.Single().Message, Does.Not.Contain("private-value"));
    }

    [Test]
    public void UnexpectedSuccessCannotSatisfyAnExpectedError()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ss14-negative-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var application = new DiagnosticApplication(directory, new ScenarioBridge());
            application.AddTarget(new DiagnosticTarget("test-server", "server", "http://127.0.0.1:1212/", new string('x', 32)));
            var document = JsonNode.Parse("""{"id":"negative-test","targetId":"test-server","steps":[{"id":"read","command":"session_get","expectError":"expected_error_not_observed"}]}""")!.AsObject();
            var failure = Assert.ThrowsAsync<DiagnosticOperationFailedException>(async () => await application.Scenarios.RunAsync(document, CancellationToken.None));
            Assert.That(failure!.Fault.Code, Is.EqualTo("expected_error_not_observed"));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class ScenarioBridge : IDiagnosticBridge
    {
        public readonly List<string> Commands = [];
        public Task<DiagnosticResponse> ExecuteAsync(DiagnosticTarget target, DiagnosticRequest request, CancellationToken cancellationToken)
        {
            Commands.Add(request.Command);
            return Task.FromResult(new DiagnosticResponse
            {
                RequestId = request.RequestId, ServerInstanceId = "test-instance", WorldGeneration = 1, Source = "server", CapturedTick = 1,
                Data = request.Command switch
                {
                    "lease_acquire" => new JsonObject { ["token"] = new string('s', 32) },
                    "fixture_create" => new JsonObject { ["fixtureId"] = "fixture-one" },
                    _ => new JsonObject(),
                },
            });
        }
    }
}
