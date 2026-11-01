// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Text;
using System.Text.Json;
using Content.Meta.Commerce;
using Content.Server.Database;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Identity;

namespace Content.Server._NC.Meta.Commerce;

public sealed partial class NCMetaMarketplaceSystem
{
    private sealed record SoftwareAcknowledgement(int Version, string Actor, string Operation, string InputFingerprint,
        NCMetaSoftwareCommand? Command = null, NCMetaSoftwareQuote? Quote = null);
    private static string Acknowledgement(Context context, string operation, string input,
        IReadOnlyList<NCAtomicLedgerParticipantWrite> writes, NCMetaSoftwareQuote? quote = null)
    {
        var entry = writes.SingleOrDefault(write => write.AggregateType == CommandType);
        var command = entry == null ? null : JsonSerializer.Deserialize<NCMetaSoftwareCommand>(entry.SnapshotPayload, Json);
        var body = JsonSerializer.Serialize(new SoftwareAcknowledgement(1, context.Actor, operation, input, command, quote), Json);
        if (Encoding.UTF8.GetByteCount(body) > 16384) throw new System.IO.InvalidDataException("Software acknowledgement limit.");
        return body;
    }
    private async Task<(bool Found, SoftwareAcknowledgement? Value)> ReadAcknowledgement(Context context, Guid request,
        string operation, string input)
    {
        var commandId = NCPersistentIdentity.Command("meta", "software-" + operation, context.Actor + ":" + request);
        var stored = await _database.GetNCPersistentCommandResultAsync(commandId);
        if (stored == null) return (false, null);
        try
        {
            var payload = stored.ResultPayload;
            var paid = stored.CommandType == "living-city.ledger.post";
            if (paid && payload != null)
            {
                using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 24 });
                payload = document.RootElement.TryGetProperty("participantResultPayload", out var value) ? value.GetString() : null;
            }
            if (payload == null) return (false, null); // Legacy paid result had no participant acknowledgement.
            if (Encoding.UTF8.GetByteCount(payload) > 16384) return (true, null);
            var result = JsonSerializer.Deserialize<SoftwareAcknowledgement>(payload, Json);
            // Legacy commands used a separate current-state aggregate and a {} batch result.
            if (result is not { Version: 1 }) return (false, null);
            if (result.Actor != context.Actor || result.Operation != operation || result.InputFingerprint != input) return (true, null);
            var transaction = NCPersistentIdentity.Transaction("meta", "software-" + operation, context.Actor + ":" + request);
            var expected = paid ? NCEconomyLedgerSystem.ParticipantResultFingerprint(transaction.Value, payload)! : input;
            if (!await _persistence.CanReplayCommandAsync(stored, commandId, paid ? "living-city.ledger.post" : "meta.software." + operation,
                    context.Actor, expected, paid ? "transaction" : "meta.owner", paid ? transaction.Value.ToString("D") : null)) return (true, null);
            if (result.Command is { } receipt && (!MetaSoftwarePolicy.Digest(receipt.Fingerprint) || operation != "purchase" && receipt.Fingerprint != input || receipt.LicenseId == Guid.Empty ||
                operation is "purchase" or "resale" && receipt.TransactionId != transaction.Value ||
                !stored.Aggregates.Any(participant => participant.AggregateType == LicenseType && participant.AggregateId == receipt.LicenseId.ToString("D"))))
                return (true, null);
            if (result.Quote is { } quote && (!ValidSnapshot(quote, QuoteType, quote.PoolSlot is { } slot ? MetaQuotePoolPolicy.SlotKey(slot) : quote.Id.ToString("D"), quote.Revision) ||
                quote.Buyer != context.Actor || !stored.Aggregates.Any(participant => participant.AggregateType == QuoteType &&
                    participant.AggregateId == (quote.PoolSlot is { } index ? MetaQuotePoolPolicy.SlotKey(index) : quote.Id.ToString("D")) && participant.AggregateVersion == quote.Revision + (operation == "purchase" ? 1 : 0))))
                return (true, null);
            if (operation == "quote" && result.Quote is { } acknowledgedQuote)
            {
                var active = await _persistence.ReadQuoteAsync("software", context.Actor, acknowledgedQuote.Id);
                if (active != null)
                {
                    var current = JsonSerializer.Deserialize<NCMetaSoftwareQuote>(active.Row.Payload, Json);
                    if (current == null || current.Id != acknowledgedQuote.Id ||
                        !ValidSnapshot(current, QuoteType, active.Row.AggregateId, active.Row.AggregateVersion) ||
                        PurchaseFingerprint(current) != PurchaseFingerprint(acknowledgedQuote)) return (true, null);
                }
            }
            return await Main(() => Current(context)) ? (true, result) : (true, null);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or System.IO.InvalidDataException) { return (true, null); }
    }
    private static string PurchaseInput(Context context, Guid quote) => MetaSoftwarePolicy.Fingerprint(context.Actor, quote.ToString("D"));
}
