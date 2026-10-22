// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

#if TOOLS && NC_DIAGNOSTICS
using System.Linq;
using System.Text.Json.Nodes;
using Content.Shared.Input;
using NightCity.Diagnostics.Contracts;
using Robust.Shared.Input;
using Robust.Shared.Map;

namespace Content.Client._NC.Diagnostics;

public sealed partial class NCDiagnosticsClientSystem
{
    private readonly Dictionary<BoundKeyFunction, TimeSpan> _heldInput = new();

    private JsonObject ExecuteInput(JsonObject args)
    {
        if (!_canInput)
            throw new DiagnosticException("access_denied", "This client only permits observation.");
        var name = args.RequiredString("function", 64);
        var function = name switch
        {
            "move_up" => EngineKeyFunctions.MoveUp,
            "move_down" => EngineKeyFunctions.MoveDown,
            "move_left" => EngineKeyFunctions.MoveLeft,
            "move_right" => EngineKeyFunctions.MoveRight,
            "use" => EngineKeyFunctions.Use,
            "activate_in_hand" => ContentKeyFunctions.UseItemInHand,
            "drop" => ContentKeyFunctions.Drop,
            "swap_hands" => ContentKeyFunctions.SwapHands,
            _ => throw new DiagnosticException("unsupported_input", "The input function is not in the playtest catalogue."),
        };
        var state = args["state"]?.GetValue<string>() ?? "press";
        if (state is not ("press" or "down" or "up"))
            throw new DiagnosticException("invalid_arguments", "Input state must be press, down or up.");
        if (state == "up")
        {
            SendInput(function, BoundKeyState.Up, null);
            _heldInput.Remove(function);
        }
        else
        {
            var target = args["netEntity"] != null ? ResolveEntity(args) : (EntityUid?)null;
            SendInput(function, BoundKeyState.Down, target);
            var duration = args.BoundedInt("durationMilliseconds", state == "press" ? 40 : 200, 1, 1000);
            _heldInput[function] = _timing.RealTime + TimeSpan.FromMilliseconds(duration);
        }
        return new JsonObject { ["submitted"] = true, ["function"] = name, ["state"] = state,
            ["executionPath"] = "client-input-system", ["tick"] = _timing.CurTick.Value,
            ["outcomeConfirmed"] = false };
    }

    private void SendInput(BoundKeyFunction function, BoundKeyState state, EntityUid? target)
    {
        if (_players.LocalEntity is not { } actor || _players.LocalSession == null)
        {
            if (state == BoundKeyState.Up)
                return;
            throw new DiagnosticException("player_not_attached", "The client has no controlled entity.");
        }
        var uid = target ?? actor;
        var coordinates = _transforms.ToCoordinates(actor, _transforms.GetMapCoordinates(uid));
        var message = new ClientFullInputCmdMessage(_timing.CurTick, _timing.TickFraction,
            _inputManager.NetworkBindMap.KeyFunctionID(function), coordinates,
            new ScreenCoordinates(0, 0, default), state, uid);
        _input.HandleInputCommand(_players.LocalSession, function, message);
    }

    private void ReleaseExpiredInput()
    {
        foreach (var key in _heldInput.Where(x => x.Value <= _timing.RealTime).Select(x => x.Key).ToArray())
        {
            SendInput(key, BoundKeyState.Up, null);
            _heldInput.Remove(key);
        }
    }

    private void ReleaseAllInput()
    {
        foreach (var key in _heldInput.Keys.ToArray())
            SendInput(key, BoundKeyState.Up, null);
        _heldInput.Clear();
    }
}

#endif
