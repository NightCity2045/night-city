using Content.Server.CrewManifest;
using Content.Server.Station.Systems;
using Content.Shared.CartridgeLoader;
using Content.Shared.CartridgeLoader.Cartridges;
using Content.Shared.CCVar;
using Content.Shared.CrewManifest;
using Robust.Shared.Configuration;
using Robust.Shared.Prototypes;
// NC - Agent DIRECTORY uses persistent Night City personnel data.
using Content.Shared._NC.Agent.Shards;
using Content.Shared._NC.Agent.Shards.Directory;

namespace Content.Server.CartridgeLoader.Cartridges;

public sealed partial class CrewManifestCartridgeSystem : EntitySystem
{
    [Dependency] private CartridgeLoaderSystem _cartridgeLoader = default!;
    [Dependency] private IConfigurationManager _configManager = default!;
    [Dependency] private CrewManifestSystem _crewManifest = default!;
    [Dependency] private StationSystem _stationSystem = default!;

    private static readonly EntProtoId CartridgePrototypeName = "CrewManifestCartridge";

    /// <summary>
    /// Flag that shows that if crew manifest is allowed to be viewed from 'unsecure' entities,
    /// which is the keys for the cartridge.
    /// </summary>
    private bool _unsecureViewersAllowed = true;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CrewManifestCartridgeComponent, CartridgeMessageEvent>(OnUiMessage);
        SubscribeLocalEvent<CrewManifestCartridgeComponent, CartridgeUiReadyEvent>(OnUiReady);
        SubscribeLocalEvent<ProgramInstallationAttempt>(OnInstallationAttempt);
        Subs.CVar(_configManager, CCVars.CrewManifestUnsecure, OnCrewManifestUnsecureChanged, true);
    }

    /// <summary>
    /// The ui messages received here get wrapped by a CartridgeMessageEvent and are relayed from the <see cref="CartridgeLoaderSystem"/>
    /// </summary>
    /// <remarks>
    /// The cartridge specific ui message event needs to inherit from the CartridgeMessageEvent
    /// </remarks>
    private void OnUiMessage(EntityUid uid, CrewManifestCartridgeComponent component, CartridgeMessageEvent args)
    {
        UpdateUiState(uid, GetEntity(args.LoaderUid), component);
    }

    /// <summary>
    /// This gets called when the ui fragment needs to be updated for the first time after activating
    /// </summary>
    private void OnUiReady(EntityUid uid, CrewManifestCartridgeComponent component, CartridgeUiReadyEvent args)
    {
        UpdateUiState(uid, args.Loader, component);
    }

    private void UpdateUiState(EntityUid uid, EntityUid loaderUid, CrewManifestCartridgeComponent? component)
    {
        if (!Resolve(uid, ref component))
            return;

        // NC - The Agent-specific system owns DIRECTORY state; ordinary PDA cartridges remain vanilla.
        if (HasComp<NCAgentDirectoryComponent>(uid))
            return;

        var owningStation = _stationSystem.GetOwningStation(uid);

        if (owningStation is null)
        {
            // Display "loading failed" message
            var failureMessage = Loc.GetString("crew-manifest-cartridge-loading-failed");
            var failureState = new CrewManifestUiState(failureMessage, new CrewManifestEntries());
            _cartridgeLoader.UpdateCartridgeUiState(loaderUid, failureState);
            return;
        }

        var (stationName, entries) = _crewManifest.GetCrewManifest(owningStation.Value);

        var state = new CrewManifestUiState(stationName, entries);
        _cartridgeLoader.UpdateCartridgeUiState(loaderUid, state);
    }

    private void OnInstallationAttempt(ref ProgramInstallationAttempt args)
    {
        // NC - DIRECTORY reads a separate public Night City registry and is not governed by the station manifest CVar.
        if (args.Prototype == CartridgePrototypeName &&
            !_unsecureViewersAllowed &&
            !HasComp<NCAgentShardStorageComponent>(args.LoaderUid))
            args.Cancelled = true;
    }

    private void OnCrewManifestUnsecureChanged(bool unsecureViewersAllowed)
    {
        _unsecureViewersAllowed = unsecureViewersAllowed;

        // NC start - DIRECTORY is a physical Agent shard and must never be silently installed on every loader.
        // The CVar still controls whether installed shards may query the public manifest.
        if (_unsecureViewersAllowed)
            return;

        var allCartridgeLoaders = AllEntityQuery<CartridgeLoaderComponent>();
        while (allCartridgeLoaders.MoveNext(out var loaderUid, out var comp))
        {
            // NC - Never remove a physical DIRECTORY shard from an Agent because the station CVar changed.
            if (HasComp<NCAgentShardStorageComponent>(loaderUid))
                continue;

            if (_cartridgeLoader.TryGetProgram<CrewManifestCartridgeComponent>((loaderUid, comp)) is { } program)
                _cartridgeLoader.UninstallProgram((loaderUid, comp), program);
        }
        // NC end
    }
}
