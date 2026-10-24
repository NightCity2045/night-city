// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Content.Shared._NC.Persistence.Time;

namespace Content.Server.Database;

/// <summary>Content attestations authored by the atomic DB writer, never by a guest or domain caller.</summary>
public static class NCMetaJournalIntegrity
{
    public const string Field = "_ncMetaSnapshot";
    public const int MaximumTailEvents = 4096;
    public const int MaximumPageEvents = 128;
    public static readonly string OriginHash = new('0', 64);
    public sealed record Attestation(int Version, int FormatVersion, string PayloadHash, string PreviousHash, string CurrentHash);

    /// <summary>A fixed-size proof cursor. History length is not a lifetime limit on confirmed aggregates.</summary>
    public sealed class TailVerifier
    {
        private readonly string _type;
        private readonly string _id;
        private readonly long _boundaryVersion;
        private readonly string _boundaryHash;
        private long _expectedVersion;
        private string _expectedHash;
        private bool _failed;
        public long Remaining => _expectedVersion - _boundaryVersion;
        public long NextVersion => _expectedVersion;
        public bool Complete => !_failed && Remaining == 0 && _expectedHash == _boundaryHash;

        private TailVerifier(NCAggregateSnapshotData current, NCAggregateSnapshotData? baseline)
        {
            _type = current.AggregateType;
            _id = current.AggregateId;
            _expectedVersion = current.AggregateVersion;
            _expectedHash = HashSnapshot(current);
            _boundaryVersion = baseline?.AggregateVersion ?? 0;
            _boundaryHash = baseline == null ? OriginHash : HashSnapshot(baseline);
        }

        public static TailVerifier? Create(NCAggregateSnapshotData current, NCAggregateSnapshotData? baseline) =>
            ValidRange(current, baseline) ? new(current, baseline) : null;

        /// <summary>Consumes one bounded newest-first page. A failed or incomplete chain never becomes authority.</summary>
        public bool ConsumePage(IReadOnlyList<NCPersistentEventData> page, CancellationToken cancellationToken = default)
        {
            if (_failed || page.Count is < 1 or > MaximumPageEvents || page.Count > Remaining)
            { _failed = true; return false; }
            foreach (var entry in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryPreviousHash(entry, _type, _id, _expectedVersion, _expectedHash, out var previous))
                { _failed = true; return false; }
                _expectedHash = previous;
                _expectedVersion--;
            }
            if (Remaining == 0 && _expectedHash != _boundaryHash) _failed = true;
            return !_failed;
        }
    }

    public static string HashPayload(string payload) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    private static string HashState(string type, string id, long version, int format, Guid epoch, long ticks, string payloadHash)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
        {
            writer.Write(type); writer.Write(id); writer.Write(version); writer.Write(format);
            writer.Write(epoch.ToByteArray()); writer.Write(ticks); writer.Write(payloadHash);
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int) bytes.Length))));
    }

    public static string HashSnapshot(NCAggregateSnapshotData snapshot) => HashState(snapshot.AggregateType,
        snapshot.AggregateId, snapshot.AggregateVersion, snapshot.FormatVersion, snapshot.SessionEpoch.Value,
        snapshot.ActiveWorldTime.Ticks, HashPayload(snapshot.Payload));

    public static string Attest(string type, string id, long version, int format, Guid epoch, long ticks,
        string snapshotPayload, string eventPayload, NCAggregateSnapshot? previous)
    {
        if (!type.StartsWith("meta.", StringComparison.Ordinal)) return eventPayload;
        return AttestSnapshot(type, id, version, format, epoch, ticks, snapshotPayload, eventPayload, previous == null ? null : new(previous.AggregateType,
            previous.AggregateId, previous.AggregateVersion, previous.FormatVersion, new NCSessionEpoch(previous.SessionEpoch),
            new NCActiveWorldTime(previous.ActiveWorldTicks), previous.Payload, previous.CreatedAt));
    }

    public static string AttestSnapshot(string type, string id, long version, int format, Guid epoch, long ticks,
        string snapshotPayload, string eventPayload, NCAggregateSnapshotData? previous)
    {
        if (!type.StartsWith("meta.", StringComparison.Ordinal)) return eventPayload;
        var payloadHash = HashPayload(snapshotPayload);
        var proof = new Attestation(1, format, payloadHash, previous == null ? OriginHash : HashSnapshot(previous),
            HashState(type, id, version, format, epoch, ticks, payloadHash));
        using var document = JsonDocument.Parse(eventPayload);
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            writer.WriteStartObject();
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                    if (property.Name != Field) property.WriteTo(writer);
            }
            else
            {
                writer.WritePropertyName("_ncMetaEvent");
                document.RootElement.WriteTo(writer);
            }
            writer.WritePropertyName(Field);
            JsonSerializer.Serialize(writer, proof);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, checked((int) bytes.Length));
    }

    /// <summary>Checks every version and exact content link. Legacy events can only precede an immutable baseline.</summary>
    public static bool CanValidateTail(NCAggregateSnapshotData current, NCAggregateSnapshotData? baseline,
        IReadOnlyList<NCPersistentEventData> newestFirst)
    {
        var version = baseline?.AggregateVersion ?? 0;
        if (!ValidRange(current, baseline) || current.AggregateVersion - version > MaximumTailEvents ||
            newestFirst.Count != current.AggregateVersion - version) return false;
        var expected = HashSnapshot(current);
        var expectedVersion = current.AggregateVersion;
        foreach (var entry in newestFirst)
        {
            if (!TryPreviousHash(entry, current.AggregateType, current.AggregateId, expectedVersion--, expected, out var previous)) return false;
            expected = previous;
        }
        return expected == (baseline == null ? OriginHash : HashSnapshot(baseline));
    }

    private static bool ValidRange(NCAggregateSnapshotData current, NCAggregateSnapshotData? baseline) =>
        current is { AggregateVersion: > 0, FormatVersion: > 0, Payload: not null,
                AggregateType.Length: > 0 and <= 64, AggregateId.Length: > 0 and <= 128 } &&
        current.SessionEpoch.IsValid && current.ActiveWorldTime.IsValid &&
        (baseline == null || baseline is { AggregateVersion: > 0, FormatVersion: > 0, Payload: not null } &&
            baseline.SessionEpoch.IsValid && baseline.ActiveWorldTime.IsValid && baseline.AggregateVersion <= current.AggregateVersion &&
            baseline.AggregateType == current.AggregateType && baseline.AggregateId == current.AggregateId);

    private static bool TryPreviousHash(NCPersistentEventData entry, string type, string id, long version,
        string expected, out string previous)
    {
        previous = string.Empty;
        if (entry == null || entry.AggregateType != type || entry.AggregateId != id || entry.AggregateVersion != version ||
            !entry.SessionEpoch.IsValid || !entry.ActiveWorldTime.IsValid || entry.Payload == null) return false;
        Attestation? proof;
        try
        {
            using var document = JsonDocument.Parse(entry.Payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(Field, out var field)) return false;
            proof = field.Deserialize<Attestation>();
        }
        catch (JsonException) { return false; }
        catch (ArgumentException) { return false; }
        if (proof is not { Version: 1, FormatVersion: > 0 } || !IsHash(proof.PayloadHash) ||
            !IsHash(proof.PreviousHash) || proof.CurrentHash != expected ||
            proof.CurrentHash != HashState(entry.AggregateType, entry.AggregateId, entry.AggregateVersion,
                proof.FormatVersion, entry.SessionEpoch.Value, entry.ActiveWorldTime.Ticks, proof.PayloadHash)) return false;
        previous = proof.PreviousHash;
        return true;
    }

    private static bool IsHash(string? value) => value is { Length: 64 } &&
        System.Linq.Enumerable.All(value, character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
