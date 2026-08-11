// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using System.Threading.Tasks;
using Content.Shared._NC.Agent;
using Content.Shared._NC.Agent.Components;
using Content.Shared._NC.Agent.UI;
using Content.Shared._NC.Identity;

namespace Content.Server._NC.Agent;

/// <summary>
/// Owns CitiNet paging, deletion, mention routing, and short-lived anti-spam state.
/// Persistent feed mutations remain in the database layer.
/// </summary>
public sealed partial class NCAgentSystem
{
    private readonly Dictionary<(NCCharacterId Owner, NCAgentCitiNetOperation Operation), TimeSpan>
        _citinetCooldowns = [];

    private async void OnDeletePost(EntityUid uid, NCAgentComponent component, NCAgentDeletePostMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            !view.PostIds.Contains(args.PostId) ||
            !_processing.Add((uid, user)))
        {
            return;
        }

        try
        {
            if (!CanUseCitiNetService(view.Owner, out var failure))
            {
                view.Notice = failure;
                return;
            }

            view.Notice = await _database.DeleteNCCitiNetPostAsync(view.Owner, args.PostId)
                ? "POST DELETED"
                : "POST DELETE DENIED";
        }
        catch (Exception exception)
        {
            HandleDatabaseError(uid, user, "delete CitiNet post", exception);
        }
        finally
        {
            FinishOperation(uid, user);
            await RefreshOpenViewsAsync();
        }
    }

    private async void OnSetFeedOffset(EntityUid uid, NCAgentComponent component, NCAgentSetFeedOffsetMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) || view.ActiveApp != NCAgentApp.CitiNet)
            return;

        if (!CanUseCitiNetService(view.Owner, out var failure))
        {
            view.Notice = failure;
            await SendViewAsync(uid, user);
            return;
        }

        // Align the client request to a server-owned page boundary and cap pathological offsets.
        var pageSize = Math.Clamp(Configuration.FeedPostLimit, 1, 200);
        var requested = Math.Clamp(args.Offset, 0, 10_000);
        view.FeedOffset = requested / pageSize * pageSize;
        view.Notice = string.Empty;
        await SendViewAsync(uid, user);
    }

    private bool TryConsumeCitiNetCooldown(
        NCCharacterId owner,
        NCAgentCitiNetOperation operation,
        TimeSpan cooldown,
        out TimeSpan remaining)
    {
        var now = _gameTiming.CurTime;
        if (_citinetCooldowns.TryGetValue((owner, operation), out var nextAllowed) && nextAllowed > now)
        {
            remaining = nextAllowed - now;
            return false;
        }

        _citinetCooldowns[(owner, operation)] = now + cooldown;
        remaining = TimeSpan.Zero;
        return true;
    }

    private static string CitiNetRateLimitNotice(TimeSpan remaining)
    {
        return $"CITINET RATE LIMITED // {Math.Max(1, Math.Ceiling(remaining.TotalSeconds)):0}s";
    }

    private async Task NotifyCitiNetMentionsAsync(
        NCCharacterId author,
        string authorAlias,
        string body,
        IReadOnlySet<NCCharacterId> excluded)
    {
        var aliases = ExtractCitiNetMentions(body, Configuration.MaximumMentionsPerEntry);
        if (aliases.Count == 0)
            return;

        var recipients = await _database.ResolveNCCitiNetMentionRecipientsAsync(author, aliases);
        foreach (var recipient in recipients.Where(value => !excluded.Contains(value)))
        {
            await SendAgentNotificationAsync(
                recipient,
                NCAgentNotificationKind.CitiNetMention,
                "CITINET MENTION",
                $"{authorAlias} mentioned you",
                false,
                app: NCAgentApp.CitiNet);
        }
    }

    /// <summary>
    /// Handles both compact @Handle mentions and quoted @"Handle With Spaces" mentions without interpreting markup.
    /// </summary>
    private static IReadOnlyList<string> ExtractCitiNetMentions(string body, int maximumMentions)
    {
        var limit = Math.Clamp(maximumMentions, 0, 50);
        if (limit == 0)
            return Array.Empty<string>();

        var mentions = new HashSet<string>();
        for (var index = 0; index < body.Length && mentions.Count < limit; index++)
        {
            if (body[index] != '@' || index + 1 >= body.Length)
                continue;

            var start = index + 1;
            string raw;
            if (body[start] == '"')
            {
                start++;
                var end = body.IndexOf('"', start);
                if (end < 0)
                    continue;

                raw = body[start..end];
                index = end;
            }
            else
            {
                var end = start;
                while (end < body.Length &&
                       (char.IsLetterOrDigit(body[end]) || body[end] is '_' or '-' or '.'))
                {
                    end++;
                }

                if (end == start)
                    continue;

                raw = body[start..end];
                index = end - 1;
            }

            var normalized = NormalizeAlias(raw);
            if (normalized.Length > 0)
                mentions.Add(normalized.ToUpperInvariant());
        }

        return mentions.ToList();
    }

    private void ClearCitiNet()
    {
        _citinetCooldowns.Clear();
    }

    private enum NCAgentCitiNetOperation : byte
    {
        Post,
        Comment,
        Reaction,
    }
}
