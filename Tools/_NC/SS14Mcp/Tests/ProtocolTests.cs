// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Buffers.Binary;
using System.Text.Json.Nodes;
using NightCity.Diagnostics.Contracts;
using NightCity.Diagnostics.Core;
using NUnit.Framework;

namespace NightCity.Diagnostics.Tests;

[TestFixture]
public sealed class ProtocolTests
{
    [Test]
    public void ParentLifetimeChecksProcessStartTimeAsWellAsPid()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        Assert.That(new DiagnosticParentLifetime(process.Id, process.StartTime.ToUniversalTime().Ticks).IsAlive(), Is.True);
        Assert.That(new DiagnosticParentLifetime(process.Id, process.StartTime.ToUniversalTime().Ticks - 1).IsAlive(), Is.False);
    }
    [Test]
    public async Task FramingRoundTripPreservesIdentityAndArguments()
    {
        var request = new DiagnosticRequest { Command = "entity_inspect", Arguments = new JsonObject { ["netEntity"] = "123" } };
        await using var stream = new MemoryStream();
        await DiagnosticWire.WriteAsync(stream, request, DiagnosticProtocol.MaximumRequestBytes, CancellationToken.None);
        stream.Position = 0;
        var decoded = await DiagnosticWire.ReadAsync<DiagnosticRequest>(stream, DiagnosticProtocol.MaximumRequestBytes, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(decoded.RequestId, Is.EqualTo(request.RequestId));
            Assert.That(decoded.Arguments["netEntity"]!.GetValue<string>(), Is.EqualTo("123"));
        });
    }

    [TestCase(-1)]
    [TestCase(0)]
    [TestCase(2147483647)]
    public void OversizedFramesAreRejectedBeforeAllocation(int size)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, size);
        using var stream = new MemoryStream(bytes);
        Assert.ThrowsAsync<DiagnosticException>(async () =>
            await DiagnosticWire.ReadAsync<DiagnosticRequest>(stream, DiagnosticProtocol.MaximumRequestBytes, CancellationToken.None));
    }

    [TestCase("https://127.0.0.1:1212")]
    [TestCase("http://example.com:1212")]
    [TestCase("http://user:password@localhost:1212")]
    [TestCase("http://localhost:1212/private")]
    [TestCase("http://localhost:1212/?token=secret")]
    public void BridgeRejectsNonLocalOrCredentialedOrigins(string address) =>
        Assert.Throws<DiagnosticException>(() => DiagnosticBridge.ValidateHttpAddress(address));

    [Test]
    public void LogCursorReportsOverwrittenRecordsAndAdvancesAcrossFilters()
    {
        var logs = new DiagnosticLogBuffer(2);
        logs.Add("info", "first", "one");
        logs.Add("error", "second", "two");
        logs.Add("info", "third", "three");
        var page = logs.Read(0, 10, "error");
        Assert.Multiple(() =>
        {
            Assert.That(page.Dropped, Is.EqualTo(1));
            Assert.That(page.Entries.Select(x => x.Message), Is.EqualTo(new[] { "two" }));
            Assert.That(page.NextCursor, Is.EqualTo(3));
            Assert.That(logs.Read(page.NextCursor).Entries, Is.Empty);
        });
    }

    [Test]
    public void LogCaptureRedactsCredentials()
    {
        var logs = new DiagnosticLogBuffer();
        logs.Add("info", "test", "token=abc password:xyz authorization=Bearer hidden");
        var message = logs.Read(0).Entries.Single().Message;
        Assert.That(message, Does.Not.Contain("abc").And.Not.Contain("xyz").And.Not.Contain("hidden"));
    }

    [Test]
    public void AuthenticationRejectsMissingAndWrongTokens()
    {
        var secret = new string('a', 64);
        Assert.Multiple(() =>
        {
            Assert.That(DiagnosticWire.Authenticate(secret, secret), Is.True);
            Assert.That(DiagnosticWire.Authenticate(secret, new string('b', 64)), Is.False);
            Assert.That(DiagnosticWire.Authenticate(secret, null), Is.False);
            Assert.That(DiagnosticWire.Authenticate("short", "short"), Is.False);
        });
    }

    [Test]
    public void WorkspaceRejectsTraversalAndSiblingPrefix()
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new WorkspacePaths(root);
            Assert.Throws<DiagnosticException>(() => paths.Resolve("../outside"));
            Assert.Throws<DiagnosticException>(() => paths.Resolve(root + "-sibling/file"));
            Assert.That(paths.Resolve("artifacts/result.json"), Does.StartWith(root));
        }
        finally
        {
            Directory.Delete(root);
        }
    }
}
