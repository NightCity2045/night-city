// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

namespace Content.Server._NC.Agent;

/// <summary>
/// The Agent: calls, messages, contacts, CitiNet feed, missions, contracts and the physical PIN lock of the Night City PDA.
/// </summary>
public sealed partial class NCAgentSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        InitializePersistence();
        InitializeCalls();
        InitializeCommunication();
        InitializeLocks();
        InitializeMissions();
        InitializeContracts();
        InitializeTraumaTeam();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        // Only the small active-call set is inspected; this never scans world entities.
        UpdateCallTimeouts();
        UpdateCommunicationTimeouts();
        UpdateAgentLocks();
        UpdateMapTracking();
    }
}
