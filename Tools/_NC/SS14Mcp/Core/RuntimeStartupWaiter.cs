// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using NightCity.Diagnostics.Contracts;

namespace NightCity.Diagnostics.Core;

/// <summary>Bounds cold environment startup independently of gameplay and individual bridge request deadlines.</summary>
internal static class RuntimeStartupWaiter
{
    internal static async Task WaitAsync(IDiagnosticBridge bridge, DiagnosticTarget target, Func<bool> processExited,
        CancellationToken cancellationToken, TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var until = clock.GetUtcNow().AddMinutes(6);
        string lastFault = "No bridge response.";
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processExited())
                throw new DiagnosticException("process_exited", "A game process exited during startup. Read environment logs.");
            try
            {
                var response = await bridge.ExecuteAsync(target, new DiagnosticRequest
                {
                    Command = "session_get", DeadlineUnixMilliseconds = clock.GetUtcNow().AddSeconds(2).ToUnixTimeMilliseconds(),
                }, cancellationToken);
                if (response.Error == null && (target.Kind != "client" || response.Data?["userId"] != null))
                    return;
                lastFault = response.Error == null ? "Client has no connected user yet." : response.Error.Code + ": " + response.Error.Message;
            }
            catch (DiagnosticException exception) when (exception.Code is "bridge_unavailable" or "timeout")
            { lastFault = exception.Code + ": " + exception.Message; }
            await Task.Delay(TimeSpan.FromMilliseconds(500), clock, cancellationToken);
        } while (clock.GetUtcNow() < until);
        throw new DiagnosticException("startup_timeout", "The diagnostic bridge did not become ready within six minutes. Last response: " + lastFault);
    }
}
