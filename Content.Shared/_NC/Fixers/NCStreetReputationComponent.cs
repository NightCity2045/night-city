// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Robust.Shared.GameStates;

namespace Content.Shared._NC.Fixers;

/// <summary>
/// The street reputation of the character this body belongs to, sent only to its owner for the character window.
/// The server keeps the authoritative value; this is a read-only mirror.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState(raiseAfterAutoHandleState: true)]
public sealed partial class NCStreetReputationComponent : Component
{
    public override bool SendOnlyToOwner => true;

    [AutoNetworkedField]
    public int Reputation;
}
