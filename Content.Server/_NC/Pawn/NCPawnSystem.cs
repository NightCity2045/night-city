// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.Server._NC.Bank;
using Content.Server.GameTicking;
using Content.Shared._NC.Medical.Anatomy;
using Content.Shared._NC.Pawn;
using Content.Shared._NC.Police.Components;
using Content.Shared._NC.Police.UI;
using Content.Shared._NC.Shops;
using Content.Shared.Damage.Components;
using Content.Shared.GameTicking;
using Content.Shared.IdentityManagement;
using Content.Shared.Mobs.Components;
using Robust.Shared.Containers;
using Content.Shared.Materials;
using Content.Shared.Popups;
using Content.Shared.Power.Components;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Research.Prototypes;
using Content.Shared.Stacks;
using Robust.Server.Audio;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Pawn;

/// <summary>
/// NPC buyers. A seller piles goods on the buyer's pallet (crates and bags count with their contents), talks to the
/// buyer and sells; the buyer prices each item from the street value table, hands over its share in cash and
/// destroys the goods. Every sale goes into a round log that
/// the NCPD records console can read.
/// </summary>
public sealed partial class NCPawnSystem : EntitySystem
{
    private static readonly SoundSpecifier SaleSound = new SoundPathSpecifier("/Audio/Effects/kaching.ogg");
    private const float RefreshInterval = 0.5f;
    private const int MaximumLog = 500;

    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IComponentFactory _factory = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedBatterySystem _battery = default!;
    [Dependency] private AudioSystem _audio = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private NCBankSystem _bank = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private Content.Shared.Damage.Systems.DamageableSystem _damage = default!;

    private readonly List<NCPawnLogEntry> _log = new();
    private readonly HashSet<(EntityUid Shop, EntityUid User)> _viewers = new();
    private readonly Dictionary<EntProtoId, float> _retail = new();
    private readonly Dictionary<(string, string), bool> _ancestry = new();
    private float _refresh;

    public IReadOnlyList<NCPawnLogEntry> Entries => _log;

    /// <summary>Cheapest retail price per unit of an item the city's shops sell.</summary>
    public bool TryGetRetail(EntProtoId item, out float perUnit) => _retail.TryGetValue(item, out perUnit);

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCPawnShopComponent, BoundUIOpenedEvent>(OnOpened);
        SubscribeLocalEvent<NCPawnShopComponent, BoundUIClosedEvent>(OnClosed);
        SubscribeLocalEvent<NCPawnShopComponent, NCPawnSellMessage>(OnSell);
        SubscribeLocalEvent<NCPawnShopComponent, NCPawnSellAllMessage>(OnSellAll);
        SubscribeLocalEvent<NCPoliceRecordsConsoleComponent, NCPolicePawnLogRequestMessage>(OnLogRequest);
        SubscribeLocalEvent<NCLatheProducedEvent>(OnLatheProduced);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        IndexRetail();
    }

    private void OnRoundRestart(RoundRestartCleanupEvent args)
    {
        _log.Clear();
        _viewers.Clear();
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<NCShopCatalogPrototype>())
            IndexRetail();
        if (args.WasModified<EntityPrototype>())
            _ancestry.Clear();
    }

    private void IndexRetail()
    {
        _retail.Clear();
        foreach (var catalog in _prototypes.EnumeratePrototypes<NCShopCatalogPrototype>())
        {
            foreach (var entry in catalog.Entries)
            {
                // Shops price the whole prototype; a stack prototype is priced per its spawned count.
                var units = _prototypes.TryIndex(entry.Item, out var item) &&
                            item.TryGetComponent<StackComponent>(out var stack, _factory)
                    ? Math.Max(1, stack.Count)
                    : 1;
                var perUnit = entry.Price / (float) units;
                if (!_retail.TryGetValue(entry.Item, out var existing) || perUnit < existing)
                    _retail[entry.Item] = perUnit;
            }
        }
    }

    private void OnLatheProduced(ref NCLatheProducedEvent args)
    {
        EnsureComp<NCPrintedItemComponent>(args.Item).Recipe = args.Recipe;
    }

    #region Pricing

    /// <summary>Street value of an item before the buyer's share and its condition; 0 if no buyer takes it.</summary>
    public int GetStreetValue(EntityUid item, NCPawnPricesPrototype prices)
    {
        if (IsRefused(item, prices) || MetaData(item).EntityPrototype is not { } prototype)
            return 0;

        var count = TryComp<StackComponent>(item, out var stack) ? Math.Max(1, stack.Count) : 1;
        if (prices.Items.TryGetValue(prototype.ID, out var exact))
            return exact * count;
        if (TryComp<NCPrintedItemComponent>(item, out var printed) && printed.Recipe is { } recipe &&
            _prototypes.TryIndex(recipe, out var recipePrototype))
        {
            return Math.Max(1, (int) MathF.Round(MaterialValue(recipePrototype.Materials.Select(
                pair => (pair.Key.Id, pair.Value)), prices) * prices.PrintedMarkup));
        }

        if (_retail.TryGetValue(prototype.ID, out var retail))
            return Math.Max(1, (int) MathF.Floor(retail * prices.RetailShare * count));
        if (HasComp<MaterialComponent>(item) && TryComp<PhysicalCompositionComponent>(item, out var composition))
        {
            return Math.Max(1, (int) MathF.Round(MaterialValue(composition.MaterialComposition.Select(
                pair => (pair.Key, pair.Value)), prices) * count));
        }

        foreach (var category in prices.Categories)
        {
            if (Matches(item, prototype, category))
                return category.Price * count;
        }

        return prices.Junk * count;
    }

    /// <summary>What this buyer pays for the item as it is now.</summary>
    public int GetPayout(Entity<NCPawnShopComponent> shop, EntityUid item)
    {
        if (!_prototypes.TryIndex(shop.Comp.Prices, out var prices))
            return 0;
        var value = GetStreetValue(item, prices);
        if (value <= 0)
            return 0;
        return Math.Max(1, (int) MathF.Floor(value * Condition(item) * shop.Comp.Payout));
    }

    private float MaterialValue(IEnumerable<(string Material, int Amount)> materials, NCPawnPricesPrototype prices)
    {
        var value = 0f;
        foreach (var (material, amount) in materials)
        {
            var perSheet = prices.Materials.TryGetValue(material, out var price) ? price : prices.MaterialFallback;
            value += perSheet * amount / 100f;
        }

        return value;
    }

    /// <summary>Worn, damaged or drained goods fetch less.</summary>
    private float Condition(EntityUid item)
    {
        var condition = 1f;
        if (TryComp<NCOrganIntegrityComponent>(item, out var integrity) && integrity.MaxIntegrity > 0f)
            condition = MathF.Min(condition, Math.Clamp(integrity.CurrentIntegrity / integrity.MaxIntegrity, 0.1f, 1f));
        if (TryComp<DamageableComponent>(item, out var damage))
            condition = MathF.Min(condition, Math.Clamp(1f - _damage.GetTotalDamage((item, damage)).Float() / 200f, 0.3f, 1f));
        if (TryComp<BatteryComponent>(item, out var battery) && battery.MaxCharge > 0f)
            condition = MathF.Min(condition, 0.6f + 0.4f * Math.Clamp(_battery.GetCharge((item, battery)) / battery.MaxCharge, 0f, 1f));
        return condition;
    }

    private bool IsRefused(EntityUid item, NCPawnPricesPrototype prices)
    {
        foreach (var name in prices.RefusedComponents)
        {
            if (_factory.TryGetRegistration(name, out var registration) && HasComp(item, registration.Type))
                return true;
        }

        return TryComp<StackComponent>(item, out var stack) && prices.RefusedStacks.Contains(stack.StackTypeId);
    }

    private bool Matches(EntityUid item, EntityPrototype prototype, NCPawnCategory category)
    {
        foreach (var name in category.Components)
        {
            if (_factory.TryGetRegistration(name, out var registration) && HasComp(item, registration.Type))
                return true;
        }

        foreach (var parent in category.Parents)
        {
            if (DescendsFrom(prototype.ID, parent))
                return true;
        }

        return false;
    }

    private bool DescendsFrom(string id, string ancestor)
    {
        if (id == ancestor)
            return true;
        if (_ancestry.TryGetValue((id, ancestor), out var known))
            return known;
        var result = false;
        if (_prototypes.TryIndex<EntityPrototype>(id, out var prototype) && prototype.Parents is { } parents)
        {
            foreach (var parent in parents)
            {
                if (DescendsFrom(parent, ancestor))
                {
                    result = true;
                    break;
                }
            }
        }

        _ancestry[(id, ancestor)] = result;
        return result;
    }

    #endregion

    #region Selling

    /// <summary>Everything on the buyer's pallets, contents of crates and bags included; never mobs or fixtures.</summary>
    public List<EntityUid> GetGoods(Entity<NCPawnShopComponent> shop)
    {
        var goods = new List<EntityUid>();
        var seen = new HashSet<EntityUid>();
        foreach (var pallet in _lookup.GetEntitiesInRange<NCPawnPalletComponent>(Transform(shop).Coordinates, shop.Comp.PalletRange))
        {
            foreach (var entity in _lookup.GetEntitiesIntersecting(pallet, LookupFlags.Dynamic | LookupFlags.Sundries))
            {
                if (entity != pallet.Owner)
                    Collect(entity, goods, seen);
            }
        }

        return goods;
    }

    private void Collect(EntityUid entity, List<EntityUid> goods, HashSet<EntityUid> seen)
    {
        if (!seen.Add(entity) || HasComp<MobStateComponent>(entity) || Transform(entity).Anchored)
            return;
        if (TryComp<ContainerManagerComponent>(entity, out var manager))
        {
            // Contents come first, so selling everything empties a crate before the crate itself is priced.
            foreach (var container in _containers.GetAllContainers(entity, manager))
            {
                foreach (var contained in container.ContainedEntities)
                    Collect(contained, goods, seen);
            }
        }

        goods.Add(entity);
    }

    private bool HoldsUnsold(EntityUid item)
    {
        if (!TryComp<ContainerManagerComponent>(item, out var manager))
            return false;
        foreach (var container in _containers.GetAllContainers(item, manager))
        {
            if (container.ContainedEntities.Any(contained => !TerminatingOrDeleted(contained)))
                return true;
        }

        return false;
    }

    public bool TrySell(Entity<NCPawnShopComponent> shop, EntityUid user, EntityUid item)
    {
        if (!CanSell(shop, user, item, out var payout))
            return false;

        Sell(shop, user, item, payout);
        return true;
    }

    public bool CanSell(Entity<NCPawnShopComponent> shop, EntityUid user, EntityUid item, out int payout)
    {
        payout = 0;
        if (TerminatingOrDeleted(item) || !GetGoods(shop).Contains(item))
            return false;
        if (HoldsUnsold(item))
        {
            _popup.PopupEntity(Loc.GetString("nc-pawn-empty-first"), shop, user);
            return false;
        }

        payout = GetPayout(shop, item);
        if (payout > 0)
            return true;
        _popup.PopupEntity(Loc.GetString("nc-pawn-refused", ("item", item)), shop, user);
        return false;
    }

    /// <summary>Sells every priced item on the pallet; a crate goes too once nothing unsold is left in it.</summary>
    public int SellAll(Entity<NCPawnShopComponent> shop, EntityUid user)
    {
        var sold = 0;
        foreach (var item in GetGoods(shop))
        {
            if (TerminatingOrDeleted(item) || HoldsUnsold(item))
                continue;
            var payout = GetPayout(shop, item);
            if (payout <= 0)
                continue;
            Sell(shop, user, item, payout);
            sold++;
        }

        return sold;
    }

    private void Sell(Entity<NCPawnShopComponent> shop, EntityUid user, EntityUid item, int payout)
    {
        var name = Name(item);
        var count = TryComp<StackComponent>(item, out var stack) ? Math.Max(1, stack.Count) : 1;
        QueueDel(item);
        _bank.GiveCashTo(user, payout);                       // paid in person, cash into the seller's hands
        _audio.PlayPvs(SaleSound, shop);
        Record(shop, user, name, count, payout);
    }

    private void Record(Entity<NCPawnShopComponent> shop, EntityUid user, string item, int count, int payout)
    {
        // The buyer writes down whoever they see in front of them: a masked seller stays unknown.
        _log.Add(new NCPawnLogEntry(_ticker.RoundDuration(), Loc.GetString(shop.Comp.Name),
            Content.Shared.IdentityManagement.Identity.Name(user, EntityManager), item, count, payout));
        if (_log.Count > MaximumLog)
            _log.RemoveAt(0);
    }

    private void OnSell(Entity<NCPawnShopComponent> ent, ref NCPawnSellMessage args)
    {
        if (TryGetEntity(args.Item, out var item))
            TrySell(ent, args.Actor, item.Value);
        SendState(ent, args.Actor);
    }

    private void OnSellAll(Entity<NCPawnShopComponent> ent, ref NCPawnSellAllMessage args)
    {
        SellAll(ent, args.Actor);
        SendState(ent, args.Actor);
    }

    #endregion

    #region UI

    private void OnOpened(Entity<NCPawnShopComponent> ent, ref BoundUIOpenedEvent args)
    {
        _viewers.Add((ent.Owner, args.Actor));
        SendState(ent, args.Actor);
    }

    private void OnClosed(Entity<NCPawnShopComponent> ent, ref BoundUIClosedEvent args)
    {
        _viewers.Remove((ent.Owner, args.Actor));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (_viewers.Count == 0)
            return;
        _refresh += frameTime;
        if (_refresh < RefreshInterval)
            return;
        _refresh = 0f;
        foreach (var (shop, user) in _viewers.ToArray())
        {
            if (TerminatingOrDeleted(shop) || TerminatingOrDeleted(user) || !TryComp<NCPawnShopComponent>(shop, out var comp))
            {
                _viewers.Remove((shop, user));
                continue;
            }

            SendState((shop, comp), user);
        }
    }

    private void SendState(Entity<NCPawnShopComponent> shop, EntityUid user)
    {
        var lines = new List<NCPawnLine>();
        foreach (var item in GetGoods(shop))
        {
            if (TerminatingOrDeleted(item))
                continue;
            lines.Add(new NCPawnLine(GetNetEntity(item), MetaData(item).EntityPrototype?.ID ?? string.Empty, Name(item),
                TryComp<StackComponent>(item, out var stack) ? Math.Max(1, stack.Count) : 1,
                GetPayout(shop, item), HasComp<ContainerManagerComponent>(item) && HoldsUnsold(item)));
        }

        _ui.ServerSendUiMessage(shop.Owner, NCPawnUiKey.Key, new NCPawnUpdateMessage(Loc.GetString(shop.Comp.Name), lines), user);
    }

    private void OnLogRequest(Entity<NCPoliceRecordsConsoleComponent> ent, ref NCPolicePawnLogRequestMessage args)
    {
        var entries = Enumerable.Reverse(_log).ToList();
        _ui.ServerSendUiMessage(ent.Owner, NCPoliceRecordsUiKey.Key, new NCPolicePawnLogMessage(entries), args.Actor);
    }

    #endregion
}
