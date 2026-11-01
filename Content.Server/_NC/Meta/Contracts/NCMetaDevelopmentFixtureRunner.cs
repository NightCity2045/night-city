// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Content.Meta.Bytecode;
using Content.Meta.Commerce;
using Content.Meta.Language;
using Content.Meta.Testing;
using Content.Meta.Verification;

namespace Content.Server._NC.Meta.Contracts;

// Only the native immutable package loader constructs this value. It is not NetSerializable,
// a guest DTO, a proof accepted from a request, or an instruction to call a live game adapter.
internal sealed record NCMetaDevelopmentFixtureInput(byte[] Container, MetaCompilationRequest AuthorizedSources,
    IReadOnlyDictionary<string, byte[]> FixturesByDigest, MetaDevelopmentDelivery Delivery);
internal sealed record NCMetaDevelopmentFixtureResult(MetaDevelopmentTestStatus Status, int Count,
    long Gas, int Slices, string TraceDigest, string FailureKey);

/// <summary>CPU execution only. The native orchestrator owns access, exact CAS, receipt and money.</summary>
internal static class NCMetaDevelopmentFixtureRunner
{
    private const int TotalMilliseconds = 1500;
    private const long TotalGas = 1_000_000;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static NCMetaDevelopmentFixtureResult Run(MetaDevelopmentTerms terms,
        NCMetaDevelopmentFixtureInput input, CancellationToken cancellation)
    {
        var count = 0; long gas = 0; var slices = 0;
        using var trace = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        NCMetaDevelopmentFixtureResult Result(MetaDevelopmentTestStatus status, string key = "") =>
            new(status, count, gas, slices, Convert.ToHexStringLower(trace.GetHashAndReset()), key);
        try
        {
            var clock = Stopwatch.StartNew();
            cancellation.ThrowIfCancellationRequested();
            if (input.Container.Length > 4 * 1024 * 1024 ||
                Sha(input.Container) != input.Delivery.ContentHash || terms.CatalogHash != input.Delivery.CatalogHash ||
                terms.RunnerPolicy != MetaDevelopmentPolicy.RunnerPolicy || input.Delivery.CompilerBuild != terms.CompilerBuild ||
                input.FixturesByDigest.Count != terms.Fixtures.Select(fixture => fixture.Digest).Distinct(StringComparer.Ordinal).Count())
                return Result(MetaDevelopmentTestStatus.InvalidFixture, "nc-meta-development-storage");
            var module = MetaBytecodeLoader.Load(input.Container);
            if (module.Manifest.ApiCatalogHash != terms.CatalogHash || terms.CatalogHash != MetaApiCatalog.Default.Hash)
                return Result(MetaDevelopmentTestStatus.Unavailable, "nc-meta-development-test-unavailable");
            // MetaTestHost currently uses Default internally. Do not claim historical execution parity
            // merely because a historical verifier catalog is registered. Preserve escrow on mismatch.
            var catalog = MetaApiCatalog.Default;
            var artifact = MetaVerifier.Verify(input.Container, catalog.CreateVerificationCatalog(module));
            var exports = new MetaCompiler(catalog).VerifyExports(input.AuthorizedSources, artifact, cancellation);
            if (artifact.Digest != input.Delivery.ContainerDigest || artifact.ContentHash != input.Delivery.ContentHash ||
                exports.SourceTreeHash != input.Delivery.SourceTreeHash || exports.ArtifactDigest != artifact.Digest ||
                artifact.ResidentMemoryBytes > terms.MaximumResidentBytes || artifact.MinimumProcessMemoryBytes > terms.MaximumProcessBytes ||
                artifact.Manifest.RequiredScopes.Except(terms.MaximumScopes, StringComparer.Ordinal).Any())
                return Result(MetaDevelopmentTestStatus.Failed, "nc-meta-development-outside-terms");
            // One terms reference is one immutable MetaTestFixture, not an arbitrary user test service.
            var fixtures = new List<MetaTestFixture>();
            foreach (var reference in terms.Fixtures.OrderBy(value => value.Path, StringComparer.Ordinal))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!input.FixturesByDigest.TryGetValue(reference.Digest, out var bytes) || bytes.Length != reference.Bytes ||
                    Sha(bytes) != reference.Digest) return Result(MetaDevelopmentTestStatus.InvalidFixture, "nc-meta-development-storage");
                var fixture = MetaTestHost.ParseFixture(Utf8.GetString(bytes));
                if (fixture.RootModule != artifact.Manifest.RootModule || fixture.ExpectedCommands == null && fixture.ExpectedLogs == null)
                    return Result(MetaDevelopmentTestStatus.InvalidFixture, "nc-meta-development-fixture");
                fixtures.Add(fixture);
            }
            var fixtureHash = Sha(Utf8.GetBytes(JsonSerializer.Serialize(terms.Fixtures.OrderBy(value => value.Path, StringComparer.Ordinal))));
            if (fixtureHash != input.Delivery.FixtureHash || fixtures.Count is < 1 or > MetaDevelopmentPolicy.MaximumFixtures)
                return Result(MetaDevelopmentTestStatus.InvalidFixture, "nc-meta-development-fixture");
            foreach (var fixture in fixtures)
            {
                cancellation.ThrowIfCancellationRequested();
                var remaining = TotalMilliseconds - (int) clock.ElapsedMilliseconds;
                if (remaining <= 0 || gas >= TotalGas) return Result(MetaDevelopmentTestStatus.LimitExceeded, "nc-meta-development-test-limited");
                var result = MetaTestHost.Run(artifact, fixture, new MetaTestLimits(MaxSlices: 2048, GasPerSlice: 512,
                    MaxGas: Math.Min(200000, TotalGas - gas), MaxMilliseconds: Math.Min(150, remaining), HeapBytes: terms.MaximumProcessBytes), cancellation);
                count++; gas = checked(gas + result.GasUsed); slices = checked(slices + result.Slices);
                // Exact structured trace digest, no client-controlled textual "passed" parsing.
                trace.AppendData(Utf8.GetBytes(JsonSerializer.Serialize(new { Index = count, result.Status, result.FailureCode,
                    result.Logs, Commands = result.Commands.Select(command => command.Trace).ToArray(), result.GasUsed, result.Slices })));
                if (!result.Passed) return Result(result.Status switch
                {
                    MetaTestStatus.Failed => MetaDevelopmentTestStatus.Failed,
                    MetaTestStatus.Faulted => MetaDevelopmentTestStatus.Faulted,
                    MetaTestStatus.LimitExceeded => MetaDevelopmentTestStatus.LimitExceeded,
                    _ => MetaDevelopmentTestStatus.InvalidFixture,
                }, "nc-meta-development-technical-failed");
            }
            return Result(MetaDevelopmentTestStatus.Passed);
        }
        catch (OperationCanceledException) { return Result(MetaDevelopmentTestStatus.Unavailable, "nc-meta-development-test-unavailable"); }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException or
            DecoderFallbackException or MetaFormatException or OverflowException)
        { return Result(MetaDevelopmentTestStatus.InvalidFixture, "nc-meta-development-storage"); }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

