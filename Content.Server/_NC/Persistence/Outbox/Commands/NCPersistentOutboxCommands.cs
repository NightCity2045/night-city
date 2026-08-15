// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using Content.Server.Administration;
using Content.Server.Database;
using Content.Shared.Administration;
using Robust.Shared.Console;

namespace Content.Server._NC.Persistence.Outbox.Commands;

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCPersistentOutboxStatusCommand : LocalizedEntityCommands
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCPersistentOutboxDispatcherSystem _dispatcher = default!;

    public override string Command => "alife_outbox_status";
    public override string Description => "Show persistent outbox backlog and active-round delivery health.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 0)
        {
            shell.WriteError($"Usage: {Command}");
            return;
        }

        var now = DateTime.UtcNow;
        var status = await _database.GetNCOutboxStatusAsync(now, Math.Max(1, _dispatcher.MaximumAttempts));
        var age = status.OldestUnprocessedAt is { } oldest
            ? TimeSpan.FromTicks(Math.Max(0, (now - oldest).Ticks)).ToString()
            : "none";
        shell.WriteLine(
            $"Outbox storage: pending={status.Pending}, ready={status.Ready}, exhausted={status.Exhausted}, " +
            $"oldestAge={age}.");
        shell.WriteLine(
            $"Dispatcher: active={_dispatcher.IsActive}, fetch={_dispatcher.FetchInFlight}, " +
            $"inFlight={_dispatcher.DeliveriesInFlight}, handlers={_dispatcher.RegisteredHandlerCount}, " +
            $"maxAttempts={_dispatcher.MaximumAttempts}.");
        shell.WriteLine(
            $"Session counters: delivered={_dispatcher.DeliveredThisSession}, " +
            $"failed={_dispatcher.FailedThisSession}, exhausted={_dispatcher.ExhaustedThisSession}.");
        if (_dispatcher.LastFetchError != null)
            shell.WriteLine($"Last fetch error: {_dispatcher.LastFetchError}");
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCPersistentOutboxInfoCommand : LocalizedEntityCommands
{
    [Dependency] private IServerDbManager _database = default!;

    public override string Command => "alife_outbox_info";
    public override string Description => "Inspect outbox delivery metadata without exposing its payload.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 1 || !Guid.TryParse(args[0], out var messageId) || messageId == Guid.Empty)
        {
            shell.WriteError($"Usage: {Command} <messageId>");
            return;
        }

        var message = await _database.GetNCOutboxMessageAsync(messageId);
        if (message == null)
        {
            shell.WriteError($"Outbox message {messageId:D} was not found.");
            return;
        }

        shell.WriteLine(
            $"{message.MessageId:D}: type={message.MessageType}, event={message.EventId:D}, " +
            $"attempts={message.Attempts}, payloadChars={message.Payload.Length}.");
        shell.WriteLine(
            $"Created={message.CreatedAt:O}, available={message.AvailableAt:O}, " +
            $"processed={message.ProcessedAt?.ToString("O") ?? "no"}.");
        if (message.LastError != null)
            shell.WriteLine($"Last error: {message.LastError}");
    }
}

[AdminCommand(AdminFlags.Server | AdminFlags.Mapping)]
public sealed partial class NCPersistentOutboxRetryCommand : LocalizedEntityCommands
{
    [Dependency] private IServerDbManager _database = default!;
    [Dependency] private NCPersistentOutboxDispatcherSystem _dispatcher = default!;

    public override string Command => "alife_outbox_retry";
    public override string Description => "Requeue one unprocessed outbox message for immediate delivery.";

    public override async void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length is < 1 or > 2 ||
            !Guid.TryParse(args[0], out var messageId) || messageId == Guid.Empty ||
            args.Length == 2 && !bool.TryParse(args[1], out _))
        {
            shell.WriteError($"Usage: {Command} <messageId> [resetAttempts=true|false]");
            return;
        }

        var resetAttempts = args.Length == 1 || bool.Parse(args[1]);
        if (!await _database.RequeueNCOutboxMessageAsync(messageId, DateTime.UtcNow, resetAttempts))
        {
            shell.WriteError(
                $"Outbox message {messageId:D} was not found, is already processed, or changed concurrently.");
            return;
        }

        _dispatcher.Wake();
        shell.WriteLine(
            $"Requeued {messageId:D}; resetAttempts={resetAttempts}, dispatcherActive={_dispatcher.IsActive}.");
    }
}
