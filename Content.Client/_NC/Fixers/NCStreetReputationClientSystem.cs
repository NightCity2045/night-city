// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using Content.Shared._NC.Fixers;
using Robust.Client.Player;

namespace Content.Client._NC.Fixers;

/// <summary>Tells the character window when the local character's street reputation changes.</summary>
public sealed class NCStreetReputationClientSystem : EntitySystem
{
    [Dependency] private IPlayerManager _player = default!;

    public event Action? ReputationChanged;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCStreetReputationComponent, AfterAutoHandleStateEvent>(OnState);
    }

    private void OnState(Entity<NCStreetReputationComponent> ent, ref AfterAutoHandleStateEvent args)
    {
        if (_player.LocalEntity == ent.Owner)
            ReputationChanged?.Invoke();
    }
}
