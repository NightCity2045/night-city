// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.Server._NC.Bank;
using Content.Shared._NC.Shops;
using Content.Shared.GameTicking;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Item;
using Content.Shared.Popups;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Shops;

/// <summary>
/// Shop vending machines. Each machine sells its catalog for money taken straight from the buyer's bank account
/// (the money leaves the economy) and holds a fixed stock that refills every round. Small goods go into the
/// buyer's hands; furniture and other big things appear at the nearest pickup point, or in front of the machine.
/// </summary>
public sealed partial class NCShopSystem : EntitySystem
{
    private const float PickupRange = 8f;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IComponentFactory _factory = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedHandsSystem _hands = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private NCBankSystem _bank = default!;

    private readonly Dictionary<EntityUid, int[]> _stock = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCShopComponent, BoundUIOpenedEvent>((ent, ref _) => UpdateUi(ent));
        SubscribeLocalEvent<NCShopComponent, NCShopBuyMessage>(OnBuy);
        SubscribeLocalEvent<NCShopComponent, ComponentShutdown>((ent, ref _) => _stock.Remove(ent));
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ => _stock.Clear());
    }

    /// <summary>What is left of each catalog line this round.</summary>
    public int[] GetStock(Entity<NCShopComponent> shop)
    {
        if (!_stock.TryGetValue(shop, out var stock))
        {
            var catalog = _prototypes.Index(shop.Comp.Catalog);
            stock = catalog.Entries.Select(entry => entry.Stock).ToArray();
            _stock[shop] = stock;
        }

        return stock;
    }

    private void OnBuy(Entity<NCShopComponent> ent, ref NCShopBuyMessage args)
    {
        var user = args.Actor;
        if (!_bank.TryGetCharacterAccount(user, out var account))
        {
            _popup.PopupEntity(Loc.GetString("nc-shop-error-no-account"), ent, user);
            return;
        }

        if (TryBuy(ent, user, account, args.Index, out var error))
            _popup.PopupEntity(Loc.GetString("nc-shop-bought"), ent, user);
        else
            _popup.PopupEntity(Loc.GetString(error), ent, user);

        UpdateUi(ent);
    }

    /// <summary>Sells one of a catalog line to the user, paid from the given account.</summary>
    public bool TryBuy(Entity<NCShopComponent> shop, EntityUid user, NCBankAccountRecord account, int index, out string error)
    {
        error = string.Empty;
        var catalog = _prototypes.Index(shop.Comp.Catalog);
        var stock = GetStock(shop);
        if (index < 0 || index >= catalog.Entries.Count)
        {
            error = "nc-shop-error-unknown";
            return false;
        }

        var entry = catalog.Entries[index];
        if (stock[index] <= 0)
        {
            error = "nc-shop-error-sold-out";
            return false;
        }

        if (!_bank.TryPayPurchase(account, entry.Price, entry.Item, user))
        {
            error = "nc-shop-error-funds";
            return false;
        }

        stock[index]--;
        Dispense(shop, user, entry.Item);
        return true;
    }

    private void Dispense(EntityUid shop, EntityUid user, EntProtoId item)
    {
        var prototype = _prototypes.Index(item);
        if (prototype.TryGetComponent<ItemComponent>(out _, _factory))
        {
            var goods = Spawn(item, Transform(user).Coordinates);
            _hands.TryPickupAnyHand(user, goods);
            return;
        }

        Spawn(item, GetPickupSpot(shop));
    }

    /// <summary>The nearest pickup point, or the tile in front of the machine.</summary>
    public EntityCoordinates GetPickupSpot(EntityUid shop)
    {
        var xform = Transform(shop);
        EntityUid? best = null;
        var bestDistance = float.MaxValue;
        var origin = _transform.GetWorldPosition(xform);
        foreach (var point in _lookup.GetEntitiesInRange<NCShopPickupPointComponent>(xform.Coordinates, PickupRange))
        {
            var distance = (_transform.GetWorldPosition(point) - origin).LengthSquared();
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = point;
            }
        }

        if (best is { } pickup)
            return Transform(pickup).Coordinates;

        // Machines face south by default; "in front" turns with them.
        var forward = xform.LocalRotation.RotateVec(new System.Numerics.Vector2(0, -1));
        return xform.Coordinates.Offset(forward);
    }

    private void UpdateUi(Entity<NCShopComponent> ent)
    {
        var catalog = _prototypes.Index(ent.Comp.Catalog);
        var stock = GetStock(ent);
        var lines = catalog.Entries
            .Select((entry, index) => new NCShopLine(index, entry.Item, entry.Price, stock[index]))
            .ToList();

        foreach (var actor in _ui.GetActors(ent.Owner, NCShopUiKey.Key).ToList())
        {
            var hasAccount = _bank.TryGetCharacterAccount(actor, out var account);
            _ui.SetUiState(ent.Owner, NCShopUiKey.Key,
                new NCShopUiState(catalog.ID, hasAccount, hasAccount ? account.Balance : 0, lines));
        }
    }
}
