// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Security.Cryptography;
using System.Text;

namespace Content.Meta.Language;

public enum MetaOperationStatus { Prepared, Committing, Running, Applied, Rejected, OutcomeUnknown }

/// <summary>Durable domain reconciliation record. A journal receipt is separate from a guest await completion.</summary>
public sealed record MetaOperationReceipt(string Id, string Owner, string DeviceId, string Kind,
    string Fingerprint, MetaOperationStatus Status, string ErrorCode, long Revision)
{
    public bool Terminal => Status is MetaOperationStatus.Applied or MetaOperationStatus.Rejected or MetaOperationStatus.OutcomeUnknown;

    public MetaOperationReceipt Transition(MetaOperationStatus status, string errorCode = "")
    {
        var allowed = Status switch
        {
            MetaOperationStatus.Prepared => status is MetaOperationStatus.Committing or MetaOperationStatus.Rejected,
            MetaOperationStatus.Committing => status is MetaOperationStatus.Running or MetaOperationStatus.Applied or MetaOperationStatus.Rejected or MetaOperationStatus.OutcomeUnknown,
            MetaOperationStatus.Running => status is MetaOperationStatus.Applied or MetaOperationStatus.Rejected or MetaOperationStatus.OutcomeUnknown,
            _ => false
        };
        if (!allowed || Revision == long.MaxValue) throw new InvalidOperationException("Invalid operation receipt transition.");
        return this with { Status = status, ErrorCode = errorCode, Revision = Revision + 1 };
    }

    /// <summary>Never interprets an incomplete durable record as permission to repeat an irreversible native action.</summary>
    public MetaOperationReceipt ReconcileInterrupted() => Status switch
    {
        MetaOperationStatus.Prepared => Transition(MetaOperationStatus.Rejected, "Cancelled"),
        MetaOperationStatus.Committing or MetaOperationStatus.Running => Transition(MetaOperationStatus.OutcomeUnknown, "OutcomeUnknown"),
        _ => this
    };

    public static bool IsValidKey(string key) => key.Length is > 0 and <= 64 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>Length framing binds the scope and every payload field without ambiguous delimiter concatenation.</summary>
    public static string HashFields(params string[] fields)
    {
        using var stream = new MemoryStream();
        var length = new byte[4];
        foreach (var field in fields)
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            stream.Write(length); stream.Write(bytes);
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}
