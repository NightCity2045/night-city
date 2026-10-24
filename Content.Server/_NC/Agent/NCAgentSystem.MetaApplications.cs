// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Shared._NC.Agent.UI;

namespace Content.Server._NC.Agent;

public sealed partial class NCAgentSystem
{
    /// <summary>Uses the existing Agent authentication and CitiNet coverage authority for META browser routes.</summary>
    public bool CanUseMetaCitiNet(EntityUid agent, EntityUid user) => TryGetView(agent, user, out _, out var view) &&
        view.ActiveApp == NCAgentApp.CitiNet && TryGetCharacterId(user, out var character) && character == view.Owner &&
        CanUseCitiNetService(character, out _);
}
