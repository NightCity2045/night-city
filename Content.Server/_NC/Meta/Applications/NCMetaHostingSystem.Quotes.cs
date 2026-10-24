// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text.Json;
using Content.Meta.Commerce;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;
using Content.Server.Database;
using Content.Shared._NC.Meta.UI;

namespace Content.Server._NC.Meta.Applications;

public sealed partial class NCMetaHostingSystem
{
    private sealed record HostingQuoteAcknowledgement(int Version, NCMetaHostingQuote Quote);
    private sealed record HostingPurchaseAcknowledgement(int Version, string Buyer, Guid QuoteId, Guid LeaseId, Guid TransactionId);
    private async Task<(bool Found, NCMetaHostingQuote? Quote)> ReadQuoteAcknowledgement(Guid request, string buyer, string input)
    {
        var stored = await _database.GetNCPersistentCommandResultAsync(request);
        if (stored == null) return (false, null);
        try
        {
            var result = stored.ResultPayload is { Length: <= 16384 } payload ? JsonSerializer.Deserialize<HostingQuoteAcknowledgement>(payload, Json) : null;
            if (result is not { Version: 1 }) return (false, null); // Legacy GUID-keyed quote.
            var quote = result.Quote;
            var key = quote.PoolSlot is { } slot ? MetaQuotePoolPolicy.SlotKey(slot) : quote.Id.ToString("D");
            if (!(quote.Buyer == buyer && Valid(quote, key, quote.Revision) &&
                stored.Aggregates.Any(row => row.AggregateType == QuoteType && row.AggregateId == key && row.AggregateVersion == quote.Revision) &&
                await _persistence.CanReplayCommandAsync(stored, request, "meta.hosting.quote", buyer, input))) return (true, null);
            var active = await _persistence.ReadQuoteAsync("hosting", buyer, quote.Id);
            if (active != null)
            {
                var current = JsonSerializer.Deserialize<NCMetaHostingQuote>(active.Row.Payload, Json);
                if (current == null || current.Id != quote.Id || !Valid(current, active.Row.AggregateId, active.Row.AggregateVersion) ||
                    JsonSerializer.Serialize(current with { Revision = quote.Revision, Consumed = quote.Consumed, LeaseId = quote.LeaseId }, Json) !=
                    JsonSerializer.Serialize(quote, Json)) return (true, null);
            }
            return (true, quote);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException) { return (true, null); }
    }
    private async Task<(bool Found, NCMetaHostingLease? Lease)> ReadPurchaseAcknowledgement(EntityUid terminal, EntityUid user,
        Guid request, string buyer, Guid quoteId)
    {
        var stored = await _database.GetNCPersistentCommandResultAsync(request);
        if (stored == null || stored.CommandType != "living-city.ledger.post") return (false, null);
        try
        {
            using var document = JsonDocument.Parse(stored.ResultPayload ?? "{}", new JsonDocumentOptions { MaxDepth = 24 });
            if (!document.RootElement.TryGetProperty("participantResultPayload", out var property) || property.ValueKind == JsonValueKind.Null) return (false, null);
            var payload = property.GetString();
            var result = payload is { Length: <= 16384 } ? JsonSerializer.Deserialize<HostingPurchaseAcknowledgement>(payload, Json) : null;
            var transaction = NCPersistentIdentity.Transaction("meta", "hosting-purchase", buyer + ":" + request);
            if (result is not { Version: 1 } || result.Buyer != buyer || result.QuoteId != quoteId || result.TransactionId != transaction.Value ||
                !stored.Aggregates.Any(row => row.AggregateType == LeaseType && row.AggregateId == result.LeaseId.ToString("D")) ||
                !await _persistence.CanReplayCommandAsync(stored, request, "living-city.ledger.post", buyer,
                    NCEconomyLedgerSystem.ParticipantResultFingerprint(transaction.Value, payload)!, "transaction", transaction.Value.ToString("D"))) return (true, null);
            var lease = await Read<NCMetaHostingLease>(LeaseType, result.LeaseId.ToString("D"));
            return lease != null && lease.Buyer == buyer && lease.QuoteId == quoteId && lease.PaymentTransactionId == transaction.Value &&
                await Main(() => _ui.IsUiOpen(terminal, NCMetaUiKey.Workspace, user) && _devices.CanManage(terminal, user) &&
                    _parties.TryResolve(user, out var current) && current.ToString() == buyer) ? (true, lease) : (true, null);
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return (true, null); }
    }
}
