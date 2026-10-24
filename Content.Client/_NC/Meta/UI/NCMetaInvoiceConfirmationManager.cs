// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Linq;
using Content.Shared._NC.Meta.UI;

namespace Content.Client._NC.Meta.UI;

/// <summary>Owns native payment dialogs outside the guest renderer and its stylesheet subtree.</summary>
public sealed class NCMetaInvoiceConfirmationManager : IDisposable
{
    private readonly Dictionary<Guid, NCMetaInvoiceWindow> _windows = [];
    private bool _disposed;
    public event Action<BoundUserInterfaceMessage>? Requested;

    public bool TryReceive(BoundUserInterfaceMessage message)
    {
        if (_disposed) return false;
        if (message is NCMetaInvoiceStatusMessage status)
        {
            if (_windows.TryGetValue(status.ConfirmationId, out var existing) && existing.SessionId == status.SessionId)
                existing.ApplyStatus(status);
            return true;
        }
        if (message is not NCMetaInvoicePromptMessage prompt) return false;
        if (prompt.ConfirmationId == Guid.Empty || prompt.SessionId == Guid.Empty || prompt.InvoiceId == Guid.Empty || prompt.InvoiceHash.Length != 64 ||
            prompt.Amount is <= 0 or > 1000000000 || prompt.Payee.Length is < 1 or > 128 || prompt.ExpiresInSeconds is < 1 or > 30)
            return true;
        if (_windows.ContainsKey(prompt.ConfirmationId)) return true;
        if (_windows.Count >= 4)
        {
            Requested?.Invoke(new NCMetaInvoiceDecisionMessage(prompt.SessionId, prompt.ConfirmationId, prompt.InvoiceId,
                prompt.InvoiceHash, prompt.Amount, prompt.Payee, false));
            return true;
        }
        var window = new NCMetaInvoiceWindow();
        window.ApplyPrompt(prompt);
        window.Decided += decision => Requested?.Invoke(decision);
        window.OnClose += () =>
        {
            _windows.Remove(prompt.ConfirmationId);
            window.Dispose();
        };
        _windows.Add(prompt.ConfirmationId, window);
        window.OpenCentered();
        return true;
    }
    public void CloseSession(Guid sessionId)
    {
        foreach (var pair in _windows.Where(x => x.Value.SessionId == sessionId).ToArray())
        { _windows.Remove(pair.Key); pair.Value.Close(); pair.Value.Dispose(); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        foreach (var window in _windows.Values.ToArray()) { window.Close(); window.Dispose(); }
        _windows.Clear();
        _disposed = true;
        Requested = null;
    }
}
