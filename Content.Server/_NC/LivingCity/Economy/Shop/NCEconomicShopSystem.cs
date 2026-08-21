// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using Content.Server.Preferences.Managers;
using Content.Server._NC.LivingCity.Identity;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.LivingCity.Economy.Shop;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared._NC.Persistence.Time;
using Robust.Server.GameObjects;
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
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    private readonly Dictionary<EntityUid, ShopRuntime> _shops = [];
    private readonly Dictionary<(EntityUid Shop, EntityUid User), ShopView> _views = [];
    private readonly HashSet<(EntityUid Shop, EntityUid User)> _processing = [];
    private readonly HashSet<EntityUid> _bootstrapping = [];
    private readonly HashSet<Task> _inFlight = [];

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
        if (_clock.IsReady)
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
        }
    }

    private void OnSessionStarted(NCActiveWorldSessionStartedEvent args)
    {
        var query = EntityQueryEnumerator<NCEconomicShopComponent>();
        while (query.MoveNext(out var uid, out var component))
            Track(BootstrapAsync(uid, Configuration(component)));
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
            _processing.Remove((uid, user));
            await SendStateAsync(uid, component, user);
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
            assetVersion = item.AggregateVersion;
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
        return "nc-economic-shop-result-success";
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
            if (Exists(uid) && TryComp<NCEconomicShopComponent>(uid, out var component))
            {
                foreach (var key in _views.Keys.Where(key => key.Shop == uid).ToArray())
                    await SendStateAsync(uid, component, key.User);
            }
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
        foreach (var seed in config.Commodities.OrderBy(seed => seed.Commodity.Id))
        {
            var source = NCPersistentIdentity.Command(
                "economic-shop", "initial-commodity-source", $"{config.ShopId}.{seed.Commodity.Id}");
            var node = await _assets.GetStockNodeAsync(nodeId, true);
            if (node == null)
                throw new InvalidOperationException("Shop stock disappeared during commodity seeding.");
            await _assets.CreateCommodityBatchAsync(new NCCreateCommodityBatchRequest(
                NCPersistentIdentity.Command(
                    "economic-shop", "initial-commodity", $"{config.ShopId}.{seed.Commodity.Id}"),
                NCPersistentIdentity.CommodityBatch(seed.Commodity.Id, source, "initial"),
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
        }

        foreach (var seed in config.Items.OrderBy(seed => seed.Definition.Id))
        {
            for (var index = 0; index < seed.Quantity; index++)
            {
                var key = $"{config.ShopId}.{seed.Definition.Id}.{index}";
                var source = NCPersistentIdentity.Command("economic-shop", "initial-item-source", key);
                var node = await _assets.GetStockNodeAsync(nodeId, true);
                if (node == null)
                    throw new InvalidOperationException("Shop stock disappeared during item seeding.");
                await _assets.CreateItemInstanceAsync(new NCCreateItemInstanceRequest(
                    NCPersistentIdentity.Command("economic-shop", "initial-item", key),
                    new NCItemId(NCPersistentIdentity.Command("economic-shop", "item-identity", key)),
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
            }
        }
    }

    private async Task SendStateAsync(EntityUid uid, NCEconomicShopComponent component, EntityUid user)
    {
        if (!_views.TryGetValue((uid, user), out var view))
            return;
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
                    var batch = await _assets.GetCommodityBatchAsync(batchId, true);
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
                        available,
                        price,
                        batch.Snapshot.Quality,
                        batch.Snapshot.ExpiresAt is { } expiresAt
                            ? TimeSpan.FromTicks(expiresAt.Ticks - now.Ticks)
                            : null));
                }
                foreach (var itemId in node.Snapshot.ItemIds.OrderBy(id => id.Value))
                {
                    if (listings.Count >= component.MaximumListings)
                        break;
                    var item = await _assets.GetItemAsync(itemId, true);
                    if (item == null || item.Snapshot.Lifecycle != NCEconomicAssetLifecycle.Active ||
                        item.Snapshot.ReservationId.HasValue ||
                        !_prototypes.TryIndex(new ProtoId<NCItemDefinitionPrototype>(item.Snapshot.DefinitionId),
                            out var definition) || definition.BasePrice is not { } price)
                        continue;
                    listings.Add(new NCEconomicShopListingSummary(
                        NCEconomicOfferKind.ItemInstance,
                        itemId.ToString(),
                        definition.ID,
                        definition.StableKey,
                        1,
                        price,
                        item.Snapshot.Condition,
                        null));
                }
            }
        }

        long balance = 0;
        var hasAccount = false;
        if (TryGetCharacter(user, out var characterId))
        {
            var account = await _finance.GetPrimaryAccountAsync(NCPartyRef.ForCharacter(characterId), true);
            if (account != null)
            {
                balance = account.Snapshot.Balance;
                hasAccount = true;
            }
        }
        _ui.SetUiState(uid, NCEconomicShopUiKey.Key, new NCEconomicShopState(
            component.DisplayName,
            balance,
            hasAccount,
            ready,
            view.Notice,
            listings));
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

    private static ShopConfiguration Configuration(NCEconomicShopComponent component) => new(
        component.ShopId.Trim().ToLowerInvariant(),
        component.DisplayName.Trim(),
        component.SellerOrganizationId.Trim().ToLowerInvariant(),
        component.SemanticLocationKey.Trim().ToLowerInvariant(),
        component.MaximumListings,
        component.InitialCommodities.Select(seed => new CommoditySeed(seed.Commodity, seed.Quantity, seed.Quality))
            .ToArray(),
        component.InitialItems.Select(seed => new ItemSeed(seed.Definition, seed.Quantity, seed.Condition)).ToArray());

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
            MaximumListings is > 0 and <= 100 && Commodities.Count <= 64 && Items.Count <= 64 &&
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
