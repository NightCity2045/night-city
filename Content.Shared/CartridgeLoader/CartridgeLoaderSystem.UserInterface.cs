using System.Linq;
// NC - Agent program relay performs VIN authorization before protected use.
using Content.Shared._NC.Agent.Components;
using Content.Shared._NC.Agent.Shards;

namespace Content.Shared.CartridgeLoader;

public sealed partial class CartridgeLoaderSystem
{
    private void OnLoaderUiMessage(Entity<CartridgeLoaderComponent> ent, ref CartridgeLoaderUiMessage message)
    {
        // NC start - Every Agent shard operation requires an authoritative unlocked device session.
        if (HasComp<NCAgentComponent>(ent))
        {
            var access = new NCAgentDeviceAccessAttemptEvent(message.Actor);
            RaiseLocalEvent(ent.Owner, ref access);
            if (access.Cancelled)
                return;
        }
        // NC end

        var cartridge = GetEntity(message.CartridgeUid);

        switch (message.Action)
        {
            case CartridgeUiMessageAction.Activate:
                ActivateProgram(ent, cartridge);
                break;
            case CartridgeUiMessageAction.Deactivate:
                DeactivateProgram(ent, cartridge);
                break;
            case CartridgeUiMessageAction.Install:
                InstallCartridge(ent, cartridge);
                break;
            case CartridgeUiMessageAction.Uninstall:
                // NC - The actor is required when a physical Agent shard returns to its item slot.
                UninstallProgram(ent, cartridge, message.Actor);
                break;
            case CartridgeUiMessageAction.UIReady:
                if (ent.Comp.ActiveProgram is { } foreground)
                {
                    var evt = new CartridgeUiReadyEvent(ent);
                    RaiseLocalEvent(foreground, ref evt);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException($"Unrecognized UI action passed from cartridge loader ui {message.Action}.");
        }
    }

    /// <summary>
    /// Relays ui messages meant for cartridges to the currently active cartridge.
    /// </summary>
    private void OnUiMessage(Entity<CartridgeLoaderComponent> ent, ref CartridgeUiMessage args)
    {
        // NC start - A still-open cartridge fragment cannot bypass a subsequently locked Agent.
        if (HasComp<NCAgentComponent>(ent))
        {
            var access = new NCAgentDeviceAccessAttemptEvent(args.Actor);
            RaiseLocalEvent(ent.Owner, ref access);
            if (access.Cancelled)
                return;
        }
        // NC end

        var cartridgeEvent = args.MessageEvent;
        cartridgeEvent.User = args.Actor;
        cartridgeEvent.LoaderUid = GetNetEntity(ent);
        cartridgeEvent.Actor = args.Actor;

        if (ent.Comp.ActiveProgram is not { } foreground)
            return;

        // NC start - Removing or replacing the VIN card revokes protected program use immediately.
        if (HasComp<NCAgentShardStorageComponent>(ent))
        {
            var authorization = new NCAgentShardAuthorizationAttemptEvent(ent, foreground);
            RaiseLocalEvent(ref authorization);
            if (authorization.Cancelled)
            {
                DeactivateProgram(ent, foreground);
                return;
            }
        }
        // NC end

        RaiseLocalEvent(foreground, cartridgeEvent);
    }

    /// <summary>
    /// Updates the programs ui state.
    /// </summary>
    /// <param name="loaderUid">The cartridge loaders entity uid.</param>
    /// <param name="state">The programs ui state. Programs should use their own ui state class inheriting from <see cref="BoundUserInterfaceState"/>.</param>
    /// <param name="loader">The cartridge loader component.</param>
    /// <remarks>
    /// This method is called "UpdateCartridgeUiState" but cartridges and a programs are the same. A cartridge is just a program as a visible item.
    /// </remarks>
    /// <seealso cref="Cartridges.NotekeeperCartridgeSystem.UpdateUiState"/>
    public void UpdateCartridgeUiState(EntityUid loaderUid, BoundUserInterfaceState state, CartridgeLoaderComponent? loader = null)
    {
        if (!Resolve(loaderUid, ref loader))
            return;

        if (_userInterface.HasUi(loaderUid, loader.UiKey))
            _userInterface.SetUiState(loaderUid, loader.UiKey, state);
    }


    /// <summary>
    /// Updates the cartridge loaders ui state.
    /// </summary>
    /// <remarks>
    /// Because the cartridge loader integrates with the ui of the entity using it, the entities ui state needs to inherit from <see cref="CartridgeLoaderUiState"/>
    /// and use this method to update its state so the cartridge loaders state can be added to it.
    /// </remarks>
    /// <seealso cref="PDA.PdaSystem.UpdatePdaUserInterface"/>
    public void UpdateUiState(Entity<CartridgeLoaderComponent?> ent)
    {
        if (!Resolve(ent, ref ent.Comp))
            return;

        if (!_userInterface.HasUi(ent.Owner, ent.Comp.UiKey))
            return;

        var programs = GetNetEntityList(GetAllPrograms(ent).ToList());
        var state = new CartridgeLoaderUiState(programs, GetNetEntity(ent.Comp.ActiveProgram));
        _userInterface.SetUiState(ent.Owner, ent.Comp.UiKey, state);
    }
}
