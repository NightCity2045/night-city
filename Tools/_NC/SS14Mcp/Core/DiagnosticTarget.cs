// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.IO.Pipes;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

public sealed record DiagnosticTarget(string Id, string Kind, string Address, string Token, string? EnvironmentId = null)
{
    public object PublicInfo() => new { Id, Kind, Address, EnvironmentId };
}

public interface IDiagnosticBridge
{
    Task<DiagnosticResponse> ExecuteAsync(DiagnosticTarget target, DiagnosticRequest request, CancellationToken cancellationToken);
}

public sealed class DiagnosticBridge : IDiagnosticBridge, IDisposable
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public async Task<DiagnosticResponse> ExecuteAsync(DiagnosticTarget target, DiagnosticRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = request.DeadlineUnixMilliseconds - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (remaining <= 0)
            throw new DiagnosticException("timeout", "The request deadline has elapsed.");
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(remaining, 30_000)));
        try
        {
            DiagnosticResponse response;
            if (target.Kind == "client")
            {
                if (!ValidPipeName(target.Address))
                    throw new DiagnosticException("invalid_target", "Invalid local pipe name.");
                await using var pipe = new NamedPipeClientStream(".", target.Address, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(timeout.Token);
                await DiagnosticWire.WriteAsync(pipe, new DiagnosticPipeRequest(target.Token, request),
                    DiagnosticProtocol.MaximumRequestBytes, timeout.Token);
                response = await DiagnosticWire.ReadAsync<DiagnosticResponse>(pipe,
                    DiagnosticProtocol.MaximumResponseBytes, timeout.Token);
            }
            else
            {
                var endpoint = ValidateHttpAddress(target.Address);
                using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, DiagnosticProtocol.HttpPath));
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Token);
                var payload = JsonSerializer.SerializeToUtf8Bytes(request, DiagnosticProtocol.JsonOptions);
                if (payload.Length > DiagnosticProtocol.MaximumRequestBytes)
                    throw new DiagnosticException("message_too_large", "The request exceeds its byte limit.");
                message.Content = new ByteArrayContent(payload);
                message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var result = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!result.IsSuccessStatusCode)
                    throw new DiagnosticException(result.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "access_denied",
                        HttpStatusCode.NotFound => "bridge_unavailable",
                        HttpStatusCode.TooManyRequests => "busy",
                        HttpStatusCode.RequestTimeout => "timeout",
                        _ => "transport_error",
                    }, $"The bridge returned HTTP {(int)result.StatusCode}.", result.StatusCode == HttpStatusCode.TooManyRequests);
                await using var stream = await result.Content.ReadAsStreamAsync(timeout.Token);
                var bytes = await DiagnosticWire.ReadBoundedAsync(stream, DiagnosticProtocol.MaximumResponseBytes, timeout.Token);
                response = JsonSerializer.Deserialize<DiagnosticResponse>(bytes, DiagnosticProtocol.JsonOptions)
                    ?? throw new DiagnosticException("invalid_response", "The bridge returned an empty response.");
            }

            if (response.ApiVersion != DiagnosticProtocol.Version || response.RequestId != request.RequestId)
                throw new DiagnosticException("protocol_mismatch", "The bridge returned incompatible response metadata.");
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DiagnosticException("timeout", "The bridge did not respond before the deadline. Check operation status before retrying a mutation.");
        }
        catch (HttpRequestException)
        {
            throw new DiagnosticException("bridge_unavailable", "Could not connect to the selected local server.", true);
        }
        catch (IOException)
        {
            throw new DiagnosticException("bridge_unavailable", "The selected local process disconnected.", true);
        }
    }

    public static Uri ValidateHttpAddress(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            !uri.IsLoopback || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != "/")
            throw new DiagnosticException("invalid_target", "Server addresses must be loopback HTTP origins without credentials, paths or query strings.");
        return uri;
    }

    public static bool ValidPipeName(string name) => name.Length is > 0 and <= 100 &&
        name.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_');

    public void Dispose() => _http.Dispose();
}
