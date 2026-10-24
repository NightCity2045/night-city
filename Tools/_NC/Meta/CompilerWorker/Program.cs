// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Content.Meta.Language;

// The parent assigns its OS resource policy before sending this process any source bytes.
// Standard output contains only the framed protocol. No ECS, host paths or credentials enter the worker.
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var request = await MetaCompilerProtocol.ReadAsync<MetaWorkerRequest>(Console.OpenStandardInput(), MetaCompilerProtocol.MaximumRequestBytes, deadline.Token);
    if (request.ProtocolVersion != MetaCompilerProtocol.Version || request.CatalogHash != MetaApiCatalog.Default.Hash) return 2;
    var result = new MetaCompiler().Compile(request.Compilation with { Limits = null }, deadline.Token);
    var response = new MetaWorkerResponse(MetaCompilerProtocol.Version, result.Artifact, result.ArtifactHash, result.CacheIdentity,
        result.Diagnostics.ToArray(), result.Analysis.ToArray());
    await MetaCompilerProtocol.WriteAsync(Console.OpenStandardOutput(), response, MetaCompilerProtocol.MaximumResponseBytes, deadline.Token);
    return 0;
}
catch (Exception exception) when (exception is IOException or OperationCanceledException or System.Text.Json.JsonException or ArgumentException)
{
    return 3;
}
