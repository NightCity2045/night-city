// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Threading;
using Content.Server._NC.Chat.Translation;
using Content.Shared.CCVar;
using Content.Shared.Chat;
using Content.Shared._NC.Chat.Translation;
using Robust.Shared.Asynchronous;
using Robust.Shared.Network;
using Robust.Shared.Utility;

namespace Content.Server.Chat.Managers;

internal sealed partial class ChatManager
{
    [Dependency] private INCChatTranslationService _ncChatTranslation = default!;
    [Dependency] private ITaskManager _ncTranslationTaskManager = default!;

    private int _ncNextTranslationMessageId;

    private bool TryDispatchTranslatedChatToOne(
        ChatChannel channel,
        string message,
        string wrappedMessage,
        NetEntity source,
        int? senderKey,
        bool hideChat,
        INetChannel client,
        Color? colorOverride,
        string? audioPath,
        float audioVolume)
    {
        if (!_ncChatTranslation.IsConfiguredForChannel(channel) ||
            !_netConfigManager.GetClientCVar(client, CCVars.NCChatTranslationPreferenceEnabled))
        {
            return false;
        }

        var targetLanguage = ResolveNCTranslationLanguage(client);
        if (targetLanguage == null)
            return false;

        ObserveNCTranslationTask(DispatchTranslatedChatToOneAsync(
            channel,
            message,
            wrappedMessage,
            source,
            senderKey,
            hideChat,
            client,
            colorOverride,
            audioPath,
            audioVolume,
            targetLanguage));
        return true;
    }

    private async Task DispatchTranslatedChatToOneAsync(
        ChatChannel channel,
        string message,
        string wrappedMessage,
        NetEntity source,
        int? senderKey,
        bool hideChat,
        INetChannel client,
        Color? colorOverride,
        string? audioPath,
        float audioVolume,
        string targetLanguage)
    {
        var translationTask = _ncChatTranslation.TranslateAsync(message, channel);
        var softHold = Math.Max(0, _configurationManager.GetCVar(CCVars.NCChatTranslationSoftHoldMs));
        var completed = softHold == 0
            ? null
            : await Task.WhenAny(translationTask, Task.Delay(softHold));

        if (completed == translationTask)
        {
            var translation = await translationTask;
            await RunNCTranslationOnMainThreadAsync(() =>
            {
                var (visible, wrapped) = BuildNCTranslatedMessage(message, wrappedMessage, translation, targetLanguage);
                SendNCChatMessage(client, channel, visible, wrapped, source, senderKey, hideChat, colorOverride, audioPath, audioVolume, null, false);
            });
            return;
        }

        var messageId = unchecked((uint) Interlocked.Increment(ref _ncNextTranslationMessageId));
        await RunNCTranslationOnMainThreadAsync(() =>
            SendNCChatMessage(client, channel, message, wrappedMessage, source, senderKey, hideChat, colorOverride, audioPath, audioVolume, messageId, false));

        var delayedTranslation = await translationTask;
        var delayed = BuildNCTranslatedMessage(message, wrappedMessage, delayedTranslation, targetLanguage);
        // Always finalize a provisional message. The client delays its speech bubble until this update, so a
        // failed or unnecessary translation must still release the original text for display above the speaker.
        await RunNCTranslationOnMainThreadAsync(() =>
            SendNCChatMessage(client, channel, delayed.Visible, delayed.Wrapped, source, senderKey, hideChat, colorOverride, null, 0f, messageId, true));
    }

    private static (string Visible, string Wrapped) BuildNCTranslatedMessage(
        string originalMessage,
        string originalWrapped,
        NCChatTranslationPayload? translation,
        string targetLanguage)
    {
        if (translation == null)
            return (originalMessage, originalWrapped);

        var visible = translation.GetVisibleText(targetLanguage);
        if (string.Equals(visible, originalMessage, StringComparison.Ordinal))
            return (originalMessage, originalWrapped);

        var escapedOriginal = FormattedMessage.EscapeText(originalMessage);
        var escapedVisible = FormattedMessage.EscapeText(visible);
        var wrapped = originalWrapped.Replace(escapedOriginal, escapedVisible, StringComparison.Ordinal);

        // A few custom chat sources provide already escaped text. Keep their prefix/suffix whenever possible.
        if (string.Equals(wrapped, originalWrapped, StringComparison.Ordinal))
            wrapped = originalWrapped.Replace(originalMessage, escapedVisible, StringComparison.Ordinal);

        if (string.Equals(wrapped, originalWrapped, StringComparison.Ordinal))
            return (originalMessage, originalWrapped);

        wrapped = $"{NCChatTranslationMarkup.BuildTagMarkup(translation.SourceLanguage, originalMessage)} {wrapped}";
        return (visible, wrapped);
    }

    private string? ResolveNCTranslationLanguage(INetChannel client)
    {
        var explicitLanguage = _netConfigManager.GetClientCVar(client, CCVars.NCChatTranslationPreferenceLanguage);
        var normalized = NCChatTranslationMarkup.NormalizeLanguageCode(explicitLanguage);
        if (normalized != null)
            return normalized;

        var culture = _netConfigManager.GetClientCVar(client, CCVars.NCClientCulture);
        return NCChatTranslationMarkup.NormalizeLanguageCode(culture);
    }

    private void SendNCChatMessage(
        INetChannel client,
        ChatChannel channel,
        string message,
        string wrappedMessage,
        NetEntity source,
        int? senderKey,
        bool hideChat,
        Color? colorOverride,
        string? audioPath,
        float audioVolume,
        uint? serverMessageId,
        bool update)
    {
        var chatMessage = new ChatMessage(
            channel,
            message,
            wrappedMessage,
            source,
            senderKey,
            hideChat,
            colorOverride,
            audioPath,
            audioVolume,
            serverMessageId);

        if (update)
            _netManager.ServerSendMessage(new MsgUpdateChatMessage { Message = chatMessage }, client);
        else
            _netManager.ServerSendMessage(new MsgChatMessage { Message = chatMessage }, client);
    }

    private void ObserveNCTranslationTask(Task task)
    {
        _ = ObserveNCTranslationTaskAsync(task);
    }

    private async Task ObserveNCTranslationTaskAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            _sawmill?.Error($"NC chat translation task failed: {exception}");
        }
    }

    private Task RunNCTranslationOnMainThreadAsync(Action action)
    {
        var completion = new TaskCompletionSource();
        _ncTranslationTaskManager.RunOnMainThread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task;
    }
}
