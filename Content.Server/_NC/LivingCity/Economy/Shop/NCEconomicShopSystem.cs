// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System.Numerics;
using Content.Server.Preferences.Managers;
using Content.Server.Construction;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Market;
using Content.Server._NC.LivingCity.Time;
using Content.Server._NC.Vehicle;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Economy.Shop;
using Content.Shared._NC.LivingCity.Market;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Content.Shared._NC.Vehicle.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.LivingCity.Economy.Shop;

/// <summary>
/// Physical access boundary for finite persistent shop stock. The client can select only an asset identity and a
/// quantity; this system authors the quote, reservation, account legs, transfer identities, and destination custody.
/// </summary>
public sealed partial class NCEconomicShopSystem : EntitySystem
{
    private const string AuditSource = "economic-shop";
    private static readonly ProtoId<NCEconomicPricingPolicyPrototype> PricingPolicy = "Default";
    private static readonly NCEconomicPriceContext NeutralPriceContext = new(0, 0, 0, 0, 0, 0);

    [Dependency] private NCEconomicAssetRegistrySystem _assets = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCPartyDirectorySystem _directory = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCMarketRegistrySystem _market = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private NCVehicleOwnershipSystem _vehicleOwnership = default!;
    [Dependency] private FlatpackSystem _flatpack = default!;

    private readonly Dictionary<EntityUid, ShopRuntime> _shops = [];
    private readonly Dictionary<(EntityUid Shop, EntityUid User), ShopView> _views = [];
    private readonly HashSet<(EntityUid Shop, EntityUid User)> _processing = [];
    private readonly HashSet<EntityUid> _bootstrapping = [];
    private readonly HashSet<Task> _inFlight = [];
    private readonly HashSet<EntityUid> _reservedVehicleZones = [];
    private readonly Dictionary<(EntityUid Shop, EntityUid User), EntityUid> _vehicleDeliveries = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCEconomicShopComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<NCEconomicShopComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCEconomicShopComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCEconomicShopComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<NCEconomicShopComponent, NCEconomicShopPurchaseMessage>(OnPurchase);
        SubscribeLocalEvent<NCActiveWorldSessionStartedEvent>(OnSessionStarted);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
    }

    private void OnMapInit(Entity<NCEconomicShopComponent> entity, ref MapInitEvent args)
    {
        if (_clock.IsReady && !entity.Comp.ExternalStock)
            Track(BootstrapAsync(entity.Owner, Configuration(entity.Comp)));
    }

    private void OnShutdown(Entity<NCEconomicShopComponent> entity, ref ComponentShutdown args)
    {
        _shops.Remove(entity.Owner);
        _bootstrapping.Remove(entity.Owner);
        foreach (var key in _views.Keys.Where(key => key.Shop == entity.Owner).ToArray())
        {
            _views.Remove(key);
            _processing.Remove(key);
            if (_vehicleDeliveries.Remove(key, out var zone))
                _reservedVehicleZones.Remove(zone);
        }
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        var query = EntityQueryEnumerator<NCEconomicShopComponent>();
        while (query.MoveNext(out var uid, out var component))
        {
            if (!component.ExternalStock)
                Track(BootstrapAsync(uid, Configuration(component)));
        }
    }

    /// <summary>
    /// Binds a temporary physical storefront to stock already owned by its seller. No stock or seller identity is
    /// created here, so closing the storefront cannot duplicate inventory.
    /// </summary>
    public async Task<bool> BindExternalStockAsync(EntityUid uid, NCPartyRef seller, NCStockNodeId stockNodeId,
        bool enabled = false)
    {
        if (!seller.IsValid || !stockNodeId.IsValid ||
            !TryComp<NCEconomicShopComponent>(uid, out var component) || !component.ExternalStock)
            return false;
        var node = await _assets.GetStockNodeAsync(stockNodeId, true);
        var account = await _finance.EnsurePrimaryAccountAsync(seller, "economic-shop-external-stock");
        if (node?.Snapshot.Owner != seller || !account.IsResolved)
            return false;
        _shops[uid] = new ShopRuntime(seller, account.AccountId, stockNodeId, enabled, null);
        await RefreshOpenViewsAsync(uid);
        return true;
    }

    public bool SetExternalStockEnabled(EntityUid uid, bool enabled)
    {
        if (!_shops.TryGetValue(uid, out var runtime) ||
            !TryComp<NCEconomicShopComponent>(uid, out var component) || !component.ExternalStock)
            return false;
        _shops[uid] = runtime with { Ready = enabled };
        Track(RefreshOpenViewsAsync(uid));
        return true;
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        if (_inFlight.Count > 0)
            args.AddPendingWrite(Task.WhenAll(_inFlight.ToArray()));
    }

    private async void OnUiOpened(
        EntityUid uid,
        NCEconomicShopComponent component,
        BoundUIOpenedEvent args)
    {
        if (args.Actor is not { Valid: true } user)
            return;
        _views[(uid, user)] = new ShopView();
        await SendStateAsync(uid, component, user);
    }

    private void OnUiClosed(
        EntityUid uid,
        NCEconomicShopComponent component,
        BoundUIClosedEvent args)
    {
        if (args.Actor is not { Valid: true } user)
            return;
        _views.Remove((uid, user));
        _processing.Remove((uid, user));
    }

    private async void OnPurchase(
        EntityUid uid,
        NCEconomicShopComponent component,
        NCEconomicShopPurchaseMessage args)
    {
        if (args.Actor is not { Valid: true } user || !_views.TryGetValue((uid, user), out var view) ||
            !_processing.Add((uid, user)))
            return;

        try
        {
            view.Notice = await PurchaseAsync(uid, user, args);
        }
        catch (Exception exception)
        {
            Log.Error($"Physical economic shop {component.ShopId} failed a purchase: {exception}");
            view.Notice = "nc-economic-shop-result-failed";
        }
        finally
        {
            if (_vehicleDeliveries.Remove((uid, user), out var zone))
                _reservedVehicleZones.Remove(zone);
            _processing.Remove((uid, user));
            await RefreshOpenViewsAsync(uid);
        }
    }

    private async Task<string> PurchaseAsync(
        EntityUid shop,
        EntityUid user,
        NCEconomicShopPurchaseMessage request)
    {
        if (!_shops.TryGetValue(shop, out var runtime) || !runtime.Ready ||
            request.Quantity <= 0 || request.Quantity > 100_000 ||
            request.Kind is not (NCEconomicOfferKind.CommodityBatch or NCEconomicOfferKind.ItemInstance) ||
            !Guid.TryParse(request.AssetId, out var assetGuid) ||
            !TryGetCharacter(user, out var characterId))
        {
            return "nc-economic-shop-result-invalid";
        }

        var buyer = NCPartyRef.ForCharacter(characterId);
        var source = await _assets.GetStockNodeAsync(runtime.StockNodeId, true);
        if (source == null || source.Snapshot.Owner != runtime.Seller)
            return "nc-economic-shop-result-unavailable";

        long assetVersion;
        NCItemDefinitionPrototype? purchasedDefinition = null;
        if (request.Kind == NCEconomicOfferKind.CommodityBatch)
        {
            var batchId = new NCCommodityBatchId(assetGuid);
            var batch = await _assets.GetCommodityBatchAsync(batchId, true);
            if (batch == null || !source.Snapshot.CommodityBatchIds.Contains(batchId) ||
                request.Quantity > batch.Snapshot.Quantity - batch.Snapshot.ReservedQuantity)
                return "nc-economic-shop-result-unavailable";
            assetVersion = batch.AggregateVersion;
        }
        else
        {
            if (request.Quantity != 1)
                return "nc-economic-shop-result-invalid";
            var itemId = new NCItemId(assetGuid);
            var item = await _assets.GetItemAsync(itemId, true);
            if (item == null || !source.Snapshot.ItemIds.Contains(itemId) || item.Snapshot.ReservationId.HasValue)
                return "nc-economic-shop-result-unavailable";
            if (!_prototypes.TryIndex(new ProtoId<NCItemDefinitionPrototype>(item.Snapshot.DefinitionId),
                    out purchasedDefinition) || purchasedDefinition.MaterializedPrototype == null)
                return "nc-economic-shop-result-unavailable";
            assetVersion = item.AggregateVersion;
            if (purchasedDefinition.Delivery == NCEconomicDeliveryKind.VehicleZone &&
                !TryReserveVehicleZone(shop, user, purchasedDefinition.MaterializedPrototype.Value))
                return "nc-economic-shop-result-delivery-zone";
        }

        var buyerAccount = await _finance.EnsurePrimaryAccountAsync(buyer, AuditSource);
        var sellerAccount = await _finance.EnsurePrimaryAccountAsync(runtime.Seller, AuditSource);
        if (!buyerAccount.IsResolved || !sellerAccount.IsResolved)
            return "nc-economic-shop-result-no-account";

        var destinationId = NCPersistentIdentity.StockNode(buyer, "character-inventory");
        var destination = await EnsureCharacterStockAsync(buyer, destinationId);
        if (destination == null)
            return "nc-economic-shop-result-destination";

        var correlation = Guid.NewGuid();
        var quoteId = NCEconomicQuoteId.New();
        var quote = await _assets.CreateQuoteAsync(new NCCreateEconomicQuoteRequest(
            Guid.NewGuid(),
            quoteId,
            request.Kind,
            assetGuid.ToString("D"),
            request.Quantity,
            runtime.Seller,
            buyer,
            runtime.StockNodeId,
            source.AggregateVersion,
            assetVersion,
            NeutralPriceContext,
            TimeSpan.FromMinutes(2),
            buyer,
            correlation,
            AuditSource: AuditSource));
        if (quote.Status is not (NCEconomicAssetMutationStatus.Applied or
            NCEconomicAssetMutationStatus.Duplicate) || quote.Quote == null)
            return "nc-economic-shop-result-quote";

        var reservationId = NCEconomicReservationId.New();
        var reservation = await _assets.CreateReservationAsync(new NCCreateEconomicReservationRequest(
            Guid.NewGuid(),
            reservationId,
            quoteId,
            quote.Quote.AggregateVersion,
            TimeSpan.FromMinutes(1),
            buyer,
            correlation,
            quote.Quote.Snapshot.LastAppliedEventId,
            AuditSource));
        if (reservation.Status is not (NCEconomicAssetMutationStatus.Applied or
            NCEconomicAssetMutationStatus.Duplicate) || reservation.Reservation == null)
            return "nc-economic-shop-result-reservation";

        destination = await _assets.GetStockNodeAsync(destinationId, true);
        buyerAccount = await _finance.GetPrimaryAccountAsync(buyer, true) is { } currentBuyer
            ? new NCPartyAccountResolution(buyer, currentBuyer.Snapshot.AccountId, currentBuyer, null)
            : buyerAccount;
        sellerAccount = await _finance.GetPrimaryAccountAsync(runtime.Seller, true) is { } currentSeller
            ? new NCPartyAccountResolution(runtime.Seller, currentSeller.Snapshot.AccountId, currentSeller, null)
            : sellerAccount;
        if (destination == null || buyerAccount.Account == null || sellerAccount.Account == null)
            return await ReleaseAsync(reservation.Reservation, buyer, correlation,
                "nc-economic-shop-result-destination");

        var receivers = await ResolveChargeReceiversAsync(quote.Quote.Snapshot);
        if (receivers.Error != null)
            return await ReleaseAsync(reservation.Reservation, buyer, correlation,
                "nc-economic-shop-result-accounts");

        var settlement = await _assets.SettleReservationAsync(new NCSettleEconomicReservationRequest(
            Guid.NewGuid(),
            NCTransactionId.New(),
            reservationId,
            reservation.Reservation.AggregateVersion,
            buyerAccount.AccountId,
            buyerAccount.Account.AggregateVersion,
            sellerAccount.AccountId,
            sellerAccount.Account.AggregateVersion,
            receivers.Fee?.AccountId,
            receivers.Fee?.Account?.AggregateVersion ?? 0,
            receivers.Tax?.AccountId,
            receivers.Tax?.Account?.AggregateVersion ?? 0,
            destinationId,
            destination.AggregateVersion,
            NCEconomicTransferId.New(),
            request.Kind == NCEconomicOfferKind.CommodityBatch &&
            request.Quantity < reservation.CommodityBatch!.Snapshot.Quantity
                ? NCCommodityBatchId.New()
                : null,
            null,
            buyer,
            correlation,
            reservation.Reservation.Snapshot.LastAppliedEventId,
            AuditSource));
        if (settlement.Status is not (NCEconomicTradeMutationStatus.Applied or
            NCEconomicTradeMutationStatus.Duplicate))
        {
            await ReleaseAsync(reservation.Reservation, buyer, correlation,
                "nc-economic-shop-result-settlement");
            return settlement.Status == NCEconomicTradeMutationStatus.InsufficientFunds
                ? "nc-economic-shop-result-funds"
                : "nc-economic-shop-result-settlement";
        }
        if (purchasedDefinition != null)
        {
            if (!TryDeliverPhysical(shop, user, purchasedDefinition))
                return "nc-economic-shop-result-delivery-pending";

            if (settlement.Transfer != null)
                await FulfillItemDemandAsync(buyer, purchasedDefinition.ID, settlement.Transfer);
        }
        return purchasedDefinition == null
            ? "nc-economic-shop-result-success"
            : "nc-economic-shop-result-success-delivered";
    }

    /// <summary>
    /// Reserves one mapper-authored bay before money moves. The broadphase check prevents a vehicle from being
    /// created over a hard fixture; the reservation also serializes simultaneous purchases against the same bay.
    /// </summary>
    private bool TryReserveVehicleZone(EntityUid shop, EntityUid user, EntProtoId vehiclePrototype)
    {
        if (!_prototypes.HasIndex<EntityPrototype>(vehiclePrototype) ||
            !TryComp<NCEconomicShopComponent>(shop, out var storefront))
            return false;

        var shopMap = Transform(shop).MapID;
        var query = EntityQueryEnumerator<NCEconomicShopVehicleDeliveryZoneComponent, TransformComponent>();
        while (query.MoveNext(out var zone, out var delivery, out var xform))
        {
            if (_reservedVehicleZones.Contains(zone) || xform.MapID != shopMap ||
                !string.Equals(delivery.ShopId, storefront.ShopId, StringComparison.OrdinalIgnoreCase) ||
                !IsVehicleZoneClear(zone, delivery, xform))
                continue;

            _reservedVehicleZones.Add(zone);
            _vehicleDeliveries[(shop, user)] = zone;
            return true;
        }

        return false;
    }

    private bool IsVehicleZoneClear(
        EntityUid zone,
        NCEconomicShopVehicleDeliveryZoneComponent delivery,
        TransformComponent xform)
    {
        var center = _transform.GetMapCoordinates(zone);
        var rotation = _transform.GetWorldRotation(zone);
        var x = rotation.RotateVec(new Vector2(delivery.HalfExtents.X, 0));
        var y = rotation.RotateVec(new Vector2(0, delivery.HalfExtents.Y));
        var extent = Vector2.Abs(x) + Vector2.Abs(y);
        foreach (var other in _lookup.GetEntitiesIntersecting(
                     center.MapId,
                     new Box2(center.Position - extent, center.Position + extent)))
        {
            if (other == zone || !TryComp<PhysicsComponent>(other, out var body) || !body.CanCollide ||
                !TryComp<FixturesComponent>(other, out var fixtures))
                continue;
            foreach (var fixture in fixtures.Fixtures.Values)
            {
                if (fixture.Hard && fixture.CollisionLayer != 0)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Spawns settled merchandise at its mapper-authored delivery point, matching the direct physical delivery
    /// used by vending machines. Vehicles additionally receive their persistent owner after spawning in a bay.
    /// </summary>
    private bool TryDeliverPhysical(
        EntityUid shop,
        EntityUid user,
        NCItemDefinitionPrototype definition)
    {
        if (definition.MaterializedPrototype is not { } prototype)
            return false;

        EntityUid spawned;
        if (definition.Delivery == NCEconomicDeliveryKind.VehicleZone)
        {
            if (!_vehicleDeliveries.TryGetValue((shop, user), out var zone) ||
                !TryComp<NCEconomicShopVehicleDeliveryZoneComponent>(zone, out var delivery) ||
                !IsVehicleZoneClear(zone, delivery, Transform(zone)))
                return false;

            spawned = Spawn(prototype, Transform(zone).Coordinates);
            _transform.SetWorldRotation(spawned, _transform.GetWorldRotation(zone));
            if (!HasComp<NCVehicleComponent>(spawned) ||
                !_vehicleOwnership.TryRegisterPurchasedVehicle(spawned, user))
            {
                QueueDel(spawned);
                return false;
            }
        }
        else
        {
            // Store consoles dispense ordinary merchandise directly onto their own tile, as vending machines do.
            spawned = Spawn(prototype, Transform(shop).Coordinates);
            if (definition.FlatpackTarget is { } target)
            {
                // Furniture listings share the generic flatpack shell; the definition supplies the deployed entity.
                if (!_flatpack.TrySetFlatpackResult(spawned, target))
                {
                    QueueDel(spawned);
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Closes the buyer's matching item demand after the durable asset transfer and physical delivery both succeed.
    /// Failure here never rolls back an already-settled purchase.
    /// </summary>
    private async Task FulfillItemDemandAsync(
        NCPartyRef buyer,
        string definitionId,
        NCEconomicAssetTransferRecord transfer)
    {
        var request = await _market.FindOpenRequestAsync(buyer, NCMarketRequestKind.UniqueItem, definitionId);
        if (request == null)
            return;
        var stable = $"{transfer.Snapshot.TransferId}.{request.Snapshot.RequestId}";
        await _market.ChangeRequestStatusAsync(new NCChangeMarketRequestStatusRequest(
            NCPersistentIdentity.Command("economic-shop", "fulfill-item-demand", stable),
            request.Snapshot.RequestId,
            request.AggregateVersion,
            NCMarketRequestStatus.Fulfilled,
            null,
            buyer,
            NCPersistentIdentity.Correlation("economic-shop", "fulfill-item-demand", stable),
            transfer.Snapshot.LastAppliedEventId,
            "economic-shop-item-demand"));
    }

    private async Task<NCStockNodeRecord?> EnsureCharacterStockAsync(NCPartyRef buyer, NCStockNodeId nodeId)
    {
        var existing = await _assets.GetStockNodeAsync(nodeId, true);
        if (existing != null)
        {
            return existing.Snapshot.Owner == buyer && existing.Snapshot.Kind == NCStockNodeKind.CharacterInventory
                ? existing
                : null;
        }
        var stable = buyer.ToString();
        var created = await _assets.CreateStockNodeAsync(new NCCreateStockNodeRequest(
            NCPersistentIdentity.Command("economic-shop", "character-stock", stable),
            nodeId,
            $"character-inventory.{buyer.PersistentId:D}",
            NCStockNodeKind.CharacterInventory,
            buyer,
            $"character.{buyer.PersistentId:D}.inventory",
            true,
            true,
            buyer,
            NCPersistentIdentity.Correlation("economic-shop", "character-stock", stable),
            AuditSource: AuditSource));
        return created.Status is NCEconomicAssetMutationStatus.Applied or NCEconomicAssetMutationStatus.Duplicate
            ? created.StockNode
            : null;
    }

    private async Task<ChargeReceivers> ResolveChargeReceiversAsync(NCEconomicQuoteSnapshot quote)
    {
        if (!_prototypes.TryIndex(PricingPolicy, out var policy))
            return new(null, null, "Pricing policy unavailable.");
        var fee = quote.Price.Fee > 0
            ? await EnsureCityServiceAccountAsync(policy.FeeReceiverServiceId)
            : null;
        var tax = quote.Price.Tax > 0
            ? policy.TaxReceiverServiceId == policy.FeeReceiverServiceId && fee != null
                ? fee
                : await EnsureCityServiceAccountAsync(policy.TaxReceiverServiceId)
            : null;
        return quote.Price.Fee > 0 && fee is not { IsResolved: true } ||
               quote.Price.Tax > 0 && tax is not { IsResolved: true }
            ? new(fee, tax, "A charge receiver account is unavailable.")
            : new(fee, tax, null);
    }

    private async Task<NCPartyAccountResolution> EnsureCityServiceAccountAsync(string serviceId)
    {
        var party = NCPersistentIdentity.CityService(serviceId);
        var registration = await _directory.RegisterAsync(new NCPartyDirectoryRegisterRequest(
            NCPersistentIdentity.Command("economy", "register-city-service", serviceId),
            party,
            serviceId,
            NCPersistentIdentity.Correlation("economy", "register-city-service", serviceId),
            AuditSource: AuditSource));
        if (registration.Status is not (NCPartyDirectoryMutationStatus.Applied or
            NCPartyDirectoryMutationStatus.Duplicate) && await _directory.GetAsync(party, true) == null)
        {
            return new NCPartyAccountResolution(party, default, null,
                registration.Error ?? "The city service could not be registered.");
        }
        return await _finance.EnsurePrimaryAccountAsync(party, AuditSource);
    }

    private async Task<string> ReleaseAsync(
        NCEconomicReservationRecord reservation,
        NCPartyRef buyer,
        Guid correlation,
        string notice)
    {
        await _assets.ReleaseReservationAsync(new NCReleaseEconomicReservationRequest(
            Guid.NewGuid(),
            reservation.Snapshot.ReservationId,
            reservation.AggregateVersion,
            NCEconomicReservationStatus.Released,
            buyer,
            correlation,
            reservation.Snapshot.LastAppliedEventId,
            AuditSource));
        return notice;
    }

    private async Task BootstrapAsync(EntityUid uid, ShopConfiguration config)
    {
        if (!_bootstrapping.Add(uid))
            return;
        try
        {
            if (!config.IsValid)
            {
                _shops[uid] = ShopRuntime.Failed("Invalid data-driven shop configuration.");
                return;
            }
            var sellerAccount = await _finance.EnsureOrganizationAsync(
                config.SellerOrganizationId, config.DisplayName, AuditSource);
            if (!sellerAccount.IsResolved)
            {
                _shops[uid] = ShopRuntime.Failed(sellerAccount.Error ?? "Seller bootstrap failed.");
                return;
            }
            var nodeId = NCPersistentIdentity.StockNode(sellerAccount.Owner, $"shop.{config.ShopId}");
            var node = await _assets.GetStockNodeAsync(nodeId, true);
            if (node == null)
            {
                var created = await _assets.CreateStockNodeAsync(new NCCreateStockNodeRequest(
                    NCPersistentIdentity.Command("economic-shop", "stock-node", config.ShopId),
                    nodeId,
                    $"shop.{config.ShopId}",
                    NCStockNodeKind.Shop,
                    sellerAccount.Owner,
                    config.SemanticLocationKey,
                    true,
                    true,
                    sellerAccount.Owner,
                    NCPersistentIdentity.Correlation("economic-shop", "stock-node", config.ShopId),
                    AuditSource: AuditSource));
                node = created.StockNode;
            }
            if (node == null || node.Snapshot.Owner != sellerAccount.Owner || node.Snapshot.Kind != NCStockNodeKind.Shop)
            {
                _shops[uid] = ShopRuntime.Failed("The persistent shop stock node is incompatible.");
                return;
            }

            await SeedAsync(config, sellerAccount.Owner, nodeId);
            _shops[uid] = new ShopRuntime(sellerAccount.Owner, sellerAccount.AccountId, nodeId, true, null);
            await RefreshOpenViewsAsync(uid);
        }
        catch (Exception exception)
        {
            Log.Error($"Failed to bootstrap economic shop {config.ShopId}: {exception}");
            _shops[uid] = ShopRuntime.Failed(exception.Message);
        }
        finally
        {
            _bootstrapping.Remove(uid);
        }
    }

    private async Task SeedAsync(ShopConfiguration config, NCPartyRef seller, NCStockNodeId nodeId)
    {
        var initialNode = await _assets.GetStockNodeAsync(nodeId, true);
        if (initialNode == null)
            throw new InvalidOperationException("Shop stock disappeared before seeding.");

        var existingBatches = initialNode.Snapshot.CommodityBatchIds.ToHashSet();
        var existingItems = initialNode.Snapshot.ItemIds.ToHashSet();
        foreach (var seed in config.Commodities.OrderBy(seed => seed.Commodity.Id))
        {
            var source = NCPersistentIdentity.Command(
                "economic-shop", "initial-commodity-source", $"{config.ShopId}.{seed.Commodity.Id}");
            var batchId = NCPersistentIdentity.CommodityBatch(seed.Commodity.Id, source, "initial");
            if (existingBatches.Contains(batchId))
                continue;

            var node = await _assets.GetStockNodeAsync(nodeId, true);
            if (node == null)
                throw new InvalidOperationException("Shop stock disappeared during commodity seeding.");
            await _assets.CreateCommodityBatchAsync(new NCCreateCommodityBatchRequest(
                NCPersistentIdentity.Command(
                    "economic-shop", "initial-commodity", $"{config.ShopId}.{seed.Commodity.Id}"),
                batchId,
                seed.Commodity,
                seed.Quantity,
                seed.Quality,
                seller,
                nodeId,
                node.AggregateVersion,
                $"shop.{config.ShopId}.initial-stock",
                seller,
                [source],
                seller,
                NCPersistentIdentity.Correlation(
                    "economic-shop", "initial-commodity", $"{config.ShopId}.{seed.Commodity.Id}"),
                AuditSource: AuditSource));
            existingBatches.Add(batchId);
        }

        foreach (var seed in config.Items.OrderBy(seed => seed.Definition.Id))
        {
            for (var index = 0; index < seed.Quantity; index++)
            {
                var key = $"{config.ShopId}.{seed.Definition.Id}.{index}";
                var itemId = SeededItemId(config.ShopId, seed.Definition, index);
                if (existingItems.Contains(itemId))
                    continue;

                var source = NCPersistentIdentity.Command("economic-shop", "initial-item-source", key);
                var node = await _assets.GetStockNodeAsync(nodeId, true);
                if (node == null)
                    throw new InvalidOperationException("Shop stock disappeared during item seeding.");
                await _assets.CreateItemInstanceAsync(new NCCreateItemInstanceRequest(
                    NCPersistentIdentity.Command("economic-shop", "initial-item", key),
                    itemId,
                    seed.Definition,
                    $"{config.ShopId}-{seed.Definition.Id}-{index + 1:D3}",
                    seed.Condition,
                    seller,
                    nodeId,
                    node.AggregateVersion,
                    $"shop.{config.ShopId}.initial-stock",
                    seller,
                    [source],
                    seller,
                    NCPersistentIdentity.Correlation("economic-shop", "initial-item", key),
                    AuditSource: AuditSource));
                existingItems.Add(itemId);
            }
        }
    }

    private async Task SendStateAsync(EntityUid uid, NCEconomicShopComponent component, EntityUid user)
    {
        if (!_views.TryGetValue((uid, user), out var view))
            return;
        // Account and stock reads are independent; do not make the balance wait behind catalog construction.
        var balanceTask = GetBalanceAsync(user);
        var listings = new List<NCEconomicShopListingSummary>();
        var now = default(NCActiveWorldTime);
        var ready = _shops.TryGetValue(uid, out var runtime) && runtime.Ready &&
                    _clock.TryGetCurrentTime(out _, out now);
        if (ready)
        {
            var node = await _assets.GetStockNodeAsync(runtime!.StockNodeId, true);
            if (node != null)
            {
                foreach (var batchId in node.Snapshot.CommodityBatchIds.OrderBy(id => id.Value))
                {
                    if (listings.Count >= component.MaximumListings)
                        break;
                    var batch = await _assets.GetCommodityBatchAsync(batchId);
                    if (batch == null || batch.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active ||
                        batch.Snapshot.ExpiresAt is { } expiry && now >= expiry ||
                        !_prototypes.TryIndex(new ProtoId<NCCommodityPrototype>(batch.Snapshot.CommodityId),
                            out var definition) || definition.BaseUnitPrice is not { } price)
                        continue;
                    var available = batch.Snapshot.Quantity - batch.Snapshot.ReservedQuantity;
                    if (available <= 0)
                        continue;
                    listings.Add(new NCEconomicShopListingSummary(
                        NCEconomicOfferKind.CommodityBatch,
                        batchId.ToString(),
                        definition.ID,
                        definition.StableKey,
                        definition.CatalogDescription?.Id ?? "nc-economic-shop-description-generic",
                        definition.CatalogCategory?.Id ?? "nc-economic-shop-category-other",
                        definition.MaterializedPrototype?.Id ?? string.Empty,
                        available,
                        price,
                        batch.Snapshot.Quality,
                        batch.Snapshot.ExpiresAt is { } expiresAt
                            ? TimeSpan.FromTicks(expiresAt.Ticks - now.Ticks)
                            : null));
                }
                var groupedItems = new Dictionary<(string Definition, int Condition),
                    (NCItemDefinitionPrototype Definition, NCItemId First, int Condition, long Count)>();
                var availableItemIds = node.Snapshot.ItemIds.ToHashSet();
                var catalogItemIds = new HashSet<NCItemId>();

                // Regular storefront stock has deterministic identities. Count it directly from the stock-node index
                // instead of issuing one database read for every physical item on every UI refresh.
                foreach (var seed in Configuration(component).Items.OrderBy(seed => seed.Definition.Id))
                {
                    if (!_prototypes.TryIndex(seed.Definition, out NCItemDefinitionPrototype? definition) ||
                        definition.BasePrice == null)
                        continue;
                    var condition = seed.Condition ?? definition.DefaultCondition;
                    for (var index = 0; index < seed.Quantity; index++)
                    {
                        var itemId = SeededItemId(component.ShopId.Trim().ToLowerInvariant(), seed.Definition, index);
                        catalogItemIds.Add(itemId);
                        if (!availableItemIds.Contains(itemId))
                            continue;
                        var key = (definition.ID, condition);
                        if (groupedItems.TryGetValue(key, out var group))
                            groupedItems[key] = (group.Definition, group.First, group.Condition, group.Count + 1);
                        else
                            groupedItems[key] = (definition, itemId, condition, 1);
                    }
                }

                // Preserve compatibility with stock inserted by other economy systems; only unknown identities need a
                // record lookup, and the registry cache avoids repeated database reads for them.
                foreach (var itemId in availableItemIds.Except(catalogItemIds).OrderBy(id => id.Value))
                {
                    var item = await _assets.GetItemAsync(itemId);
                    if (item == null || item.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active ||
                        item.Snapshot.ReservationId.HasValue ||
                        !_prototypes.TryIndex(new ProtoId<NCItemDefinitionPrototype>(item.Snapshot.DefinitionId),
                            out var definition) || definition.BasePrice is not { } price)
                        continue;

                    var key = (definition.ID, item.Snapshot.Condition);
                    if (groupedItems.TryGetValue(key, out var group))
                        groupedItems[key] = (group.Definition, group.First, group.Condition, group.Count + 1);
                    else
                        groupedItems[key] = (definition, itemId, item.Snapshot.Condition, 1);
                }

                foreach (var group in groupedItems.Values
                             .OrderBy(value => value.Definition.CatalogCategory?.Id)
                             .ThenBy(value => ResolveDisplayName(value.Definition), StringComparer.CurrentCulture))
                {
                    if (listings.Count >= component.MaximumListings)
                        break;
                    var definition = group.Definition;
                    if (definition.BasePrice is not { } price)
                        continue;
                    listings.Add(new NCEconomicShopListingSummary(
                        NCEconomicOfferKind.ItemInstance,
                        group.First.ToString(),
                        definition.ID,
                        ResolveDisplayName(definition),
                        definition.CatalogDescription?.Id ?? "nc-economic-shop-description-generic",
                        definition.CatalogCategory?.Id ?? "nc-economic-shop-category-other",
                        definition.FlatpackTarget?.Id ?? definition.MaterializedPrototype?.Id ?? string.Empty,
                        group.Count,
                        price,
                        group.Condition,
                        null));
                }
            }
        }

        var (balance, hasAccount) = await balanceTask;
        _ui.SetUiState(uid, NCEconomicShopUiKey.Key, new NCEconomicShopState(
            component.DisplayName,
            balance,
            hasAccount,
            ready,
            view.Notice,
            listings));
    }

    private async Task<(long Balance, bool HasAccount)> GetBalanceAsync(EntityUid user)
    {
        if (TryGetCharacter(user, out var characterId))
        {
            var account = await _finance.GetPrimaryAccountAsync(NCPartyRef.ForCharacter(characterId), true);
            if (account != null)
                return (account.Snapshot.Balance, true);
        }

        return (0, false);
    }

    private static NCItemId SeededItemId(
        string shopId,
        ProtoId<NCItemDefinitionPrototype> definition,
        int index)
    {
        var key = $"{shopId}.{definition.Id}.{index}";
        return new NCItemId(NCPersistentIdentity.Command("economic-shop", "item-identity", key));
    }

    /// <summary>
    /// Entity prototypes with inline names do not have an <c>ent-*</c> localization key, so resolve their
    /// authored name for the catalog instead of exposing the missing key to the player.
    /// </summary>
    private string ResolveDisplayName(NCItemDefinitionPrototype definition)
    {
        if (!definition.DisplayName.StartsWith("ent-", StringComparison.Ordinal))
            return definition.DisplayName;

        var preview = definition.FlatpackTarget ?? definition.MaterializedPrototype;
        return preview is { } prototype && _prototypes.TryIndex<EntityPrototype>(prototype, out var entity)
            ? entity.Name
            : definition.DisplayName;
    }

    /// <summary>
    /// Pushes authoritative stock and balance changes to every player currently viewing this storefront.
    /// The copied key set remains safe when a client closes the BUI while persistent reads are in flight.
    /// </summary>
    private async Task RefreshOpenViewsAsync(EntityUid shop)
    {
        foreach (var key in _views.Keys.Where(key => key.Shop == shop).ToArray())
        {
            if (Deleted(key.Shop) || Deleted(key.User) ||
                !TryComp<NCEconomicShopComponent>(key.Shop, out var component))
            {
                _views.Remove(key);
                _processing.Remove(key);
                if (_vehicleDeliveries.Remove(key, out var zone))
                    _reservedVehicleZones.Remove(zone);
                continue;
            }

            await SendStateAsync(key.Shop, component, key.User);
        }
    }

    private bool TryGetCharacter(EntityUid user, out NCCharacterId characterId)
    {
        characterId = default;
        return _players.TryGetSessionByEntity(user, out var session) &&
               _preferences.TryGetSelectedNCCharacterId(session.UserId, out characterId);
    }

    private void Track(Task task)
    {
        _inFlight.Add(task);
        _ = CompleteTrackedAsync(task);
    }

    private async Task CompleteTrackedAsync(Task task)
    {
        try
        {
            await task;
        }
        finally
        {
            _inFlight.Remove(task);
        }
    }

    private ShopConfiguration Configuration(NCEconomicShopComponent component)
    {
        // Catalog-linked definitions keep the console prototype small: product, price and stock stay together.
        var items = new Dictionary<ProtoId<NCItemDefinitionPrototype>, ItemSeed>();
        if (component.Catalog is { } catalog)
        {
            foreach (var definition in _prototypes.EnumeratePrototypes<NCItemDefinitionPrototype>())
            {
                if (definition.Abstract || definition.ShopCatalog != catalog || definition.ShopInitialQuantity <= 0)
                    continue;

                var id = new ProtoId<NCItemDefinitionPrototype>(definition.ID);
                items[id] = new ItemSeed(id, definition.ShopInitialQuantity, null);
            }
        }

        // Explicit seeds remain backwards compatible and override catalog defaults when both are present.
        foreach (var seed in component.InitialItems)
            items[seed.Definition] = new ItemSeed(seed.Definition, seed.Quantity, seed.Condition);

        return new ShopConfiguration(
            component.ShopId.Trim().ToLowerInvariant(),
            component.DisplayName.Trim(),
            component.SellerOrganizationId.Trim().ToLowerInvariant(),
            component.SemanticLocationKey.Trim().ToLowerInvariant(),
            component.MaximumListings,
            component.InitialCommodities
                .Select(seed => new CommoditySeed(seed.Commodity, seed.Quantity, seed.Quality))
                .ToArray(),
            items.Values.ToArray());
    }

    private sealed record ShopConfiguration(
        string ShopId,
        string DisplayName,
        string SellerOrganizationId,
        string SemanticLocationKey,
        int MaximumListings,
        IReadOnlyList<CommoditySeed> Commodities,
        IReadOnlyList<ItemSeed> Items)
    {
        public bool IsValid => ShopId.Length is > 0 and <= 128 && DisplayName.Length is > 0 and <= 128 &&
            SellerOrganizationId.Length is > 0 and <= 128 && SemanticLocationKey.Length is > 0 and <= 128 &&
            MaximumListings is > 0 and <= 256 && Commodities.Count <= 64 && Items.Count <= 256 &&
            Commodities.All(seed => seed.Quantity > 0) && Items.All(seed => seed.Quantity is > 0 and <= 128);
    }

    private sealed record CommoditySeed(ProtoId<NCCommodityPrototype> Commodity, long Quantity, int? Quality);
    private sealed record ItemSeed(ProtoId<NCItemDefinitionPrototype> Definition, int Quantity, int? Condition);
    private sealed record ShopRuntime(
        NCPartyRef Seller,
        NCAccountId SellerAccountId,
        NCStockNodeId StockNodeId,
        bool Ready,
        string? Error)
    {
        public static ShopRuntime Failed(string error) => new(default, default, default, false, error);
    }
    private sealed class ShopView
    {
        public string Notice = string.Empty;
    }
    private sealed record ChargeReceivers(
        NCPartyAccountResolution? Fee,
        NCPartyAccountResolution? Tax,
        string? Error);
}
