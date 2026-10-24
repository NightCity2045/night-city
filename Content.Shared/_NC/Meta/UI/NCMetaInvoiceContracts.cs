// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using Robust.Shared.Serialization;

namespace Content.Shared._NC.Meta.UI;

/// <summary>Only the native server invoice service creates this immutable payment prompt.</summary>
[Serializable, NetSerializable]
public sealed class NCMetaInvoicePromptMessage(Guid sessionId, Guid confirmationId, Guid invoiceId, string invoiceHash,
    long amount, string payee, int expiresInSeconds) : BoundUserInterfaceMessage
{
    public readonly Guid SessionId = sessionId;
    public readonly Guid ConfirmationId = confirmationId;
    public readonly Guid InvoiceId = invoiceId;
    public readonly string InvoiceHash = invoiceHash;
    public readonly long Amount = amount;
    public readonly string Payee = payee;
    public readonly int ExpiresInSeconds = expiresInSeconds;
}

[Serializable, NetSerializable]
public sealed class NCMetaInvoiceDecisionMessage(Guid sessionId, Guid confirmationId, Guid invoiceId, string invoiceHash,
    long amount, string payee, bool accept) : BoundUserInterfaceMessage
{
    public readonly Guid SessionId = sessionId;
    public readonly Guid ConfirmationId = confirmationId;
    public readonly Guid InvoiceId = invoiceId;
    public readonly string InvoiceHash = invoiceHash;
    public readonly long Amount = amount;
    public readonly string Payee = payee;
    public readonly bool Accept = accept;
}

[Serializable, NetSerializable]
public sealed class NCMetaInvoiceStatusMessage(Guid sessionId, Guid confirmationId, bool pending, bool success, string notice)
    : BoundUserInterfaceMessage
{
    public readonly Guid SessionId = sessionId;
    public readonly Guid ConfirmationId = confirmationId;
    public readonly bool Pending = pending;
    public readonly bool Success = success;
    public readonly string Notice = notice;
}
