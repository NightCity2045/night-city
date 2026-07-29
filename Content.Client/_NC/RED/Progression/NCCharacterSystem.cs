// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.
using Content.Shared._NC.Economy;
using Content.Shared._NC.RED.Progression;
using Robust.Shared.Prototypes;

namespace Content.Client._NC.RED.Progression;

public sealed partial class NCCharacterSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;

    private NCCharacterWindow? _window;
    private NCProgressionStateEvent? _state;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NCProgressionStateEvent>(OnState);
        SubscribeNetworkEvent<NCBankStateEvent>(OnBankState);
    }

    public void Open()
    {
        _window ??= new NCCharacterWindow(_prototypes, AllocateSkill);
        _window.OpenCentered();
        RaiseNetworkEvent(new NCCharacterStateRequest());
        if (_state != null)
            _window.UpdateState(_state);
    }

    private void OnState(NCProgressionStateEvent state)
    {
        _state = state;
        _window?.UpdateState(state);
    }

    private void OnBankState(NCBankStateEvent state)
    {
        if (_state == null)
            return;

        _state = new NCProgressionStateEvent(
            _state.CompletedRounds,
            _state.Level,
            _state.SpentSkillPoints,
            _state.TotalSkillPoints,
            _state.Skills,
            state.Balance,
            _state.PropertyCount,
            _state.BusinessCount,
            _state.CharacterName,
            _state.OrganizationPrototypeId,
            _state.DepartmentPrototypeId,
            _state.PositionPrototypeId,
            _state.Properties,
            _state.Businesses,
            _state.Licenses,
            _state.Documents,
            _state.LifecycleStatus,
            state.Error);
        _window?.UpdateState(_state);
    }

    private void AllocateSkill(string prototypeId, int targetRank)
    {
        RaiseNetworkEvent(new NCAllocateSkillRequest(prototypeId, targetRank, Guid.NewGuid()));
    }
}
