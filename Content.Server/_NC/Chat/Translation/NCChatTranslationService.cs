// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared._NC.Chat.Translation;
using Robust.Shared.Configuration;
using Robust.Shared.Network;

namespace Content.Server._NC.Chat.Translation;

public interface INCChatTranslationService
{
    bool IsConfiguredForChannel(ChatChannel channel);
    bool IsConfiguredForAHelp();
    Task<NCChatTranslationPayload?> TranslateAsync(string text, ChatChannel channel, CancellationToken cancel = default);
    Task<NCChatTranslationPayload?> TranslateAHelpAsync(string text, CancellationToken cancel = default);
}

public sealed record NCChatTranslationPayload(
    string OriginalText,
    string SourceLanguage,
    IReadOnlyDictionary<string, string> Translations)
{
    public string GetVisibleText(string? targetLanguage)
    {
        var target = NCChatTranslationMarkup.NormalizeLanguageCode(targetLanguage);
        if (target == null || target == SourceLanguage)
            return OriginalText;

        return Translations.TryGetValue(target, out var translated) && !string.IsNullOrWhiteSpace(translated)
            ? translated
            : OriginalText;
    }
}

/// <summary>
/// Calls the separately hosted NC translation API and deduplicates concurrent chat requests.
/// The game thread never blocks on HTTP; callers marshal completed results back before sending.
/// </summary>
public sealed class NCChatTranslationService : INCChatTranslationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IConfigurationManager _config;
    private readonly HttpClient _http;
    private readonly ISawmill _sawmill;
    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<NCChatTranslationPayload?>> _inflight = new(StringComparer.Ordinal);
    private DateTimeOffset _failureBackoffUntil;

    public NCChatTranslationService(IConfigurationManager config, IHttpClientHolder http, ILogManager logManager)
    {
        _config = config;
        _http = http.Client;
        _sawmill = logManager.GetSawmill("nc.chat.translation");
    }

    public bool IsConfiguredForChannel(ChatChannel channel)
    {
        var key = channel switch
        {
            ChatChannel.Local => "local",
            ChatChannel.Whisper => "whisper",
            ChatChannel.Radio => "radio",
            ChatChannel.LOOC => "looc",
            ChatChannel.Dead => "dead",
            ChatChannel.OOC => "ooc",
            ChatChannel.Emotes => "emotes",
            ChatChannel.AdminChat => "admin",
            _ => null,
        };

        return IsConfigured() && IsChannelEnabled(key);
    }

    public bool IsConfiguredForAHelp()
    {
        return IsConfigured() && IsChannelEnabled("ahelp");
    }

    public Task<NCChatTranslationPayload?> TranslateAsync(
        string text,
        ChatChannel channel,
        CancellationToken cancel = default)
    {
        return TranslateCoreAsync(text, IsConfiguredForChannel(channel), channel.ToString(), cancel);
    }

    public Task<NCChatTranslationPayload?> TranslateAHelpAsync(string text, CancellationToken cancel = default)
    {
        return TranslateCoreAsync(text, IsConfiguredForAHelp(), "ahelp", cancel);
    }

    private Task<NCChatTranslationPayload?> TranslateCoreAsync(
        string text,
        bool configured,
        string channel,
        CancellationToken cancel)
    {
        if (!configured || string.IsNullOrWhiteSpace(text))
            return Task.FromResult<NCChatTranslationPayload?>(null);

        var maxLength = Math.Max(1, _config.GetCVar(CCVars.NCChatTranslationMaxMessageLength));
        if (text.Length > maxLength || DateTimeOffset.UtcNow < _failureBackoffUntil)
            return Task.FromResult<NCChatTranslationPayload?>(null);

        var cacheKey = text.Trim();
        lock (_sync)
        {
            PruneCacheLocked();
            if (_cache.TryGetValue(cacheKey, out var cached))
                return Task.FromResult<NCChatTranslationPayload?>(cached.Payload);

            if (_inflight.TryGetValue(cacheKey, out var pending))
                return pending;

            var request = RequestTranslationAsync(cacheKey, channel, cancel);
            _inflight.Add(cacheKey, request);
            return request;
        }
    }

    private async Task<NCChatTranslationPayload?> RequestTranslationAsync(
        string text,
        string channel,
        CancellationToken cancel)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(Math.Max(50, _config.GetCVar(CCVars.NCChatTranslationTimeoutMs)));

            var serviceUrl = _config.GetCVar(CCVars.NCChatTranslationServiceUrl).Trim().TrimEnd('/');
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{serviceUrl}/translate")
            {
                Content = JsonContent.Create(new TranslationRequest(
                    text,
                    null,
                    [NCChatTranslationMarkup.RussianLanguageCode, NCChatTranslationMarkup.EnglishLanguageCode],
                    channel), options: JsonOptions),
            };

            var apiKey = _config.GetCVar(CCVars.NCChatTranslationApiKey);
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

            using var response = await _http.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<TranslationResponse>(JsonOptions, timeout.Token);
            var source = NCChatTranslationMarkup.NormalizeLanguageCode(result?.SourceLanguage);
            if (result == null || source == null)
                return null;

            var translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (language, translated) in result.Translations)
            {
                var normalized = NCChatTranslationMarkup.NormalizeLanguageCode(language);
                if (normalized != null && !string.IsNullOrWhiteSpace(translated))
                    translations[normalized] = translated;
            }

            var payload = new NCChatTranslationPayload(text, source, translations);
            lock (_sync)
            {
                _cache[text] = new CacheEntry(payload, DateTimeOffset.UtcNow);
                TrimCacheLocked();
            }

            return payload;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            EnterFailureBackoff($"Translation request for {channel} timed out.");
            return null;
        }
        catch (Exception exception)
        {
            EnterFailureBackoff($"Translation request for {channel} failed: {exception.Message}");
            return null;
        }
        finally
        {
            lock (_sync)
            {
                _inflight.Remove(text);
            }
        }
    }

    private bool IsConfigured()
    {
        return _config.GetCVar(CCVars.NCChatTranslationEnabled) &&
               !string.IsNullOrWhiteSpace(_config.GetCVar(CCVars.NCChatTranslationServiceUrl));
    }

    private bool IsChannelEnabled(string? key)
    {
        if (key == null)
            return false;

        var configured = _config.GetCVar(CCVars.NCChatTranslationChannels);
        var tokens = configured.Split(
            [',', ';', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return tokens.Any(token => token is "*" ||
                                   token.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                                   token.Equals(key, StringComparison.OrdinalIgnoreCase));
    }

    private void EnterFailureBackoff(string message)
    {
        _failureBackoffUntil = DateTimeOffset.UtcNow.AddSeconds(
            Math.Max(0, _config.GetCVar(CCVars.NCChatTranslationFailureBackoffSeconds)));
        _sawmill.Warning(message);
    }

    private void PruneCacheLocked()
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(1, _config.GetCVar(CCVars.NCChatTranslationCacheTtlSeconds)));
        var cutoff = DateTimeOffset.UtcNow - ttl;
        foreach (var key in _cache.Where(pair => pair.Value.CreatedAt < cutoff).Select(pair => pair.Key).ToArray())
            _cache.Remove(key);
    }

    private void TrimCacheLocked()
    {
        var maxEntries = Math.Max(1, _config.GetCVar(CCVars.NCChatTranslationCacheMaxEntries));
        if (_cache.Count <= maxEntries)
            return;

        foreach (var key in _cache.OrderBy(pair => pair.Value.CreatedAt)
                     .Take(_cache.Count - maxEntries)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _cache.Remove(key);
        }
    }

    private sealed record CacheEntry(NCChatTranslationPayload Payload, DateTimeOffset CreatedAt);
    private sealed record TranslationRequest(
        string Text,
        string? SourceLanguage,
        IReadOnlyList<string> TargetLanguages,
        string Channel);

    private sealed record TranslationResponse(
        string SourceLanguage,
        string OriginalText,
        Dictionary<string, string> Translations);
}
