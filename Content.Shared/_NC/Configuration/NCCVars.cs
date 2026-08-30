// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared;
using Robust.Shared.Configuration;

namespace Content.Shared._NC.Configuration;

/// <summary>
/// Server-only Night City configuration.
/// </summary>
[CVarDefs]
public sealed class NCCVars : CVars
{
    public static readonly CVarDef<bool> NeuralEnabled =
        CVarDef.Create("nc_neural.enabled", false, CVar.SERVERONLY);

    /// <summary>An empty value keeps the endpoint authored by the neural policy prototype.</summary>
    public static readonly CVarDef<string> NeuralEndpoint =
        CVarDef.Create("nc_neural.endpoint", string.Empty, CVar.SERVERONLY);

    /// <summary>Reserved for a future separately authenticated remote cognition gateway.</summary>
    public static readonly CVarDef<string> NeuralServiceToken =
        CVarDef.Create("nc_neural.service_token", string.Empty, CVar.SERVERONLY | CVar.CONFIDENTIAL);

    /// <summary>Provider credential forwarded only to the configured cognition endpoint as a bearer.</summary>
    public static readonly CVarDef<string> NeuralApiKey =
        CVarDef.Create("nc_neural.api_key", string.Empty, CVar.SERVERONLY | CVar.CONFIDENTIAL);

    /// <summary>Maximum number of Living City persistence operations executing against storage at once.</summary>
    public static readonly CVarDef<int> PersistenceMaximumConcurrentOperations =
        CVarDef.Create("nc_persistence.maximum_concurrent_operations", 16, CVar.SERVERONLY);

    /// <summary>Maximum number of persistence operations waiting for an execution slot.</summary>
    public static readonly CVarDef<int> PersistenceMaximumQueuedOperations =
        CVarDef.Create("nc_persistence.maximum_queued_operations", 512, CVar.SERVERONLY);

    /// <summary>Number of bounded retries for transient storage failures.</summary>
    public static readonly CVarDef<int> PersistenceMaximumRetries =
        CVarDef.Create("nc_persistence.maximum_retries", 2, CVar.SERVERONLY);

    public static readonly CVarDef<int> PersistenceInitialRetryDelayMilliseconds =
        CVarDef.Create("nc_persistence.initial_retry_delay_ms", 50, CVar.SERVERONLY);

    public static readonly CVarDef<int> PersistenceMaximumRetryDelayMilliseconds =
        CVarDef.Create("nc_persistence.maximum_retry_delay_ms", 500, CVar.SERVERONLY);

    /// <summary>Consecutive terminal failures required to temporarily open the persistence circuit.</summary>
    public static readonly CVarDef<int> PersistenceCircuitFailureThreshold =
        CVarDef.Create("nc_persistence.circuit_failure_threshold", 5, CVar.SERVERONLY);

    public static readonly CVarDef<int> PersistenceCircuitOpenSeconds =
        CVarDef.Create("nc_persistence.circuit_open_seconds", 10, CVar.SERVERONLY);
}
