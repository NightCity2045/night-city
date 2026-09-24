// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.Player;

namespace Content.Shared.Movement.Systems;

/// <summary>Vertical buttons are kept separate from the full shuttle button byte.</summary>
[Flags]
public enum NCAVButtons : byte
{
    None = 0,
    Ascend = 1 << 0,
    Descend = 1 << 1,
}

public abstract partial class SharedMoverController
{
    protected virtual void HandleNCAVInput(EntityUid uid, NCAVButtons button, bool state)
    {
    }

    private sealed class NCAVInputCmdHandler : InputCmdHandler
    {
        private readonly SharedMoverController _controller;
        private readonly NCAVButtons _button;

        public NCAVInputCmdHandler(SharedMoverController controller, NCAVButtons button)
        {
            _controller = controller;
            _button = button;
        }

        public override bool HandleCmdMessage(
            IEntityManager entManager,
            ICommonSession? session,
            IFullInputCmdMessage message)
        {
            if (session?.AttachedEntity is not { } pilot)
                return false;

            _controller.HandleNCAVInput(pilot, _button, message.State == BoundKeyState.Down);
            return false;
        }
    }
}
