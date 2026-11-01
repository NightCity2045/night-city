// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;
using NightCity.Diagnostics.Core;
using NUnit.Framework;

namespace NightCity.Diagnostics.Tests;

[TestFixture]
public sealed class ArtifactTests
{
    [Test]
    public async Task LargeReportsRemainReadableThroughExplicitJsonSelectionAndPaging()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ss14-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var app = new DiagnosticApplication(directory);
            var samples = new JsonArray(Enumerable.Range(0, 800).Select(index => (JsonNode?)new JsonObject
                { ["index"] = index, ["text"] = new string('x', 4096) }).ToArray());
            var artifact = await app.Artifacts.WriteJsonAsync("test", new JsonObject { ["samples"] = samples }, CancellationToken.None);
            var root = await app.ExecuteAsync("artifact_get", new JsonObject { ["id"] = artifact.Id }, CancellationToken.None);
            Assert.That(root!["truncated"]!.GetValue<bool>(), Is.True);
            var page = await app.ExecuteAsync("artifact_get", new JsonObject { ["id"] = artifact.Id, ["jsonPointer"] = "/samples", ["offset"] = 10, ["limit"] = 5 }, CancellationToken.None);
            Assert.That(page!["items"]!.AsArray().Count, Is.EqualTo(5));
            Assert.That(page["items"]![0]!["index"]!.GetValue<int>(), Is.EqualTo(10));
            Assert.That(page["nextOffset"]!.GetValue<int>(), Is.EqualTo(15));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public void NormalAssembliesAreRejectedAsDiagnosticGameBuildsBeforeProcessStart()
    {
        var error = Assert.Throws<DiagnosticException>(() => DiagnosticBuild.Require(typeof(DiagnosticRequest).Assembly.Location,
            "Content.Server._NC.Diagnostics", "NCDiagnosticsSystem"));
        Assert.That(error!.Code, Is.EqualTo("diagnostic_build_required"));
    }

    [Test]
    public async Task ManuallyMatchingGroupsCannotProveThatClientsBelongToTheServer()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ss14-pairing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var application = new DiagnosticApplication(directory, new MatchingReplicaBridge());
            application.AddTarget(new DiagnosticTarget("source-server", "server", "http://127.0.0.1:1212/", new string('x', 32), "declared"));
            application.AddTarget(new DiagnosticTarget("source-client", "client", "test-pipe", new string('x', 32), "declared"));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = await RuntimeSmokeTests.Complete(application, "state_compare", new JsonObject
            {
                ["targetId"] = "source-server", ["netEntity"] = "1", ["clientIds"] = new JsonArray("source-client"),
                ["paths"] = new JsonArray("/components/Door/fields/State"),
            }, deadline.Token);
            Assert.That(result!["comparison"]!["conclusive"]!.GetValue<bool>(), Is.False);
            Assert.That(result["comparison"]!["pairingCoverage"]!.GetValue<string>(), Is.EqualTo("user-declared-group-unverified"));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class MatchingReplicaBridge : IDiagnosticBridge
    {
        public Task<DiagnosticResponse> ExecuteAsync(DiagnosticTarget target, DiagnosticRequest request, CancellationToken cancellationToken)
        {
            var entity = JsonNode.Parse("""{"components":{"Door":{"fields":{"State":"Closed"}}}}""");
            return Task.FromResult(new DiagnosticResponse
            {
                RequestId = request.RequestId, ServerInstanceId = target.Id, WorldGeneration = 1, CapturedTick = 100, Source = target.Kind,
                Data = request.Command switch
                {
                    "entity_inspect" => entity,
                    "replication_inspect" => new JsonObject { ["lastRealTick"] = 100, ["entity"] = entity,
                        ["authoritativeDoorHistory"] = new JsonArray(new JsonObject { ["appliedTick"] = 100, ["State"] = "Closed" }) },
                    _ => new JsonObject(),
                },
            });
        }
    }
}
