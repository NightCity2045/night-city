// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Linq;
using Content.Shared._NC.Skill;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Client.Player;

namespace Content.Client._NC.Skill;

public sealed partial class NCSkillClientSystem : EntitySystem
{
    [Dependency] private IPlayerManager _player = default!;

    public event Action? ProgressChanged;

    public override void Initialize()
    {
        SubscribeLocalEvent<NCRequiredSkillComponent, ShotAttemptedEvent>(OnShootAttempt);
        SubscribeLocalEvent<NCRequiredSkillComponent, NCWeaponUseAttemptEvent>(OnWeaponUseAttempt);
        SubscribeLocalEvent<NCSkillPermissionsComponent, AfterAutoHandleStateEvent>(OnSkillState);
    }

    private void OnSkillState(Entity<NCSkillPermissionsComponent> character, ref AfterAutoHandleStateEvent args)
    {
        if (_player.LocalEntity == character.Owner)
            ProgressChanged?.Invoke();
    }

    private bool ShouldBlock(Entity<NCRequiredSkillComponent> weapon, EntityUid user) =>
        _player.LocalEntity == user && (!TryComp<NCSkillPermissionsComponent>(user, out var skills) ||
            !skills.Learned.Contains(weapon.Comp.Skill.Id));

    private void OnShootAttempt(Entity<NCRequiredSkillComponent> weapon, ref ShotAttemptedEvent args)
    {
        if (ShouldBlock(weapon, args.User))
            args.Cancel();
    }

    private void OnWeaponUseAttempt(Entity<NCRequiredSkillComponent> weapon, ref NCWeaponUseAttemptEvent args)
    {
        if (ShouldBlock(weapon, args.User))
            args.Cancelled = true;
    }
}
