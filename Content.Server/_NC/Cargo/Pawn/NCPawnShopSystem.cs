// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Preferences.Managers;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Time;
using Content.Shared._NC.Cargo.Pawn;
using Content.Shared._NC.Identity;
using Content.Shared._NC.LivingCity.Economy;
using Content.Shared._NC.Persistence.Identity;
using Content.Shared.ActionBlocker;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Stacks;
using Robust.Server.GameObjects;
using Robust.Shared.Asynchronous;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Server._NC.Cargo.Pawn;

/// <summary>
/// Finite physical handoff to a static pawn merchant. Pricing is the ordinary cargo appraisal times the merchant's
/// percentage. Merchandise is held in a private container while the canonical bank ledger posts the sale.
/// </summary>
public sealed partial class NCPawnShopSystem : EntitySystem
{
    private const string AuditSource = "pawn-shop-sale";
    private const string EscrowId = "nc_pawn_escrow";
    private const int MaximumEntities = 512;

    [Dependency] private PricingSystem _pricing = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCEconomyLedgerSystem _ledger = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;
    [Dependency] private SharedInteractionSystem _interaction = default!;
    [Dependency] private ActionBlockerSystem _blocker = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedTransformSystem _transform = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private ITaskManager _tasks = default!;

    private readonly Dictionary<(EntityUid Merchant, EntityUid User), Quote> _quotes = [];
    private readonly HashSet<EntityUid> _busyPallets = [];
    private readonly HashSet<Task> _operations = [];

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<NCPawnShopComponent, BoundUIOpenedEvent>(OnOpened);
        SubscribeLocalEvent<NCPawnShopComponent, BoundUIClosedEvent>(OnClosed);
        SubscribeLocalEvent<NCPawnShopComponent, NCPawnShopAppraiseMessage>(OnAppraise);
        SubscribeLocalEvent<NCPawnShopComponent, NCPawnShopSellMessage>(OnSell);
        SubscribeLocalEvent<NCPawnShopComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<NCActiveWorldSessionClosingEvent>(OnSessionClosing);
    }

    private void OnOpened(Entity<NCPawnShopComponent> ent, ref BoundUIOpenedEvent args)
        => Appraise(ent, args.Actor);

    private void OnClosed(Entity<NCPawnShopComponent> ent, ref BoundUIClosedEvent args)
        => _quotes.Remove((ent.Owner, args.Actor));

    private void OnShutdown(Entity<NCPawnShopComponent> ent, ref ComponentShutdown args)
    {
        foreach (var key in _quotes.Keys.Where(key => key.Merchant == ent.Owner).ToArray())
            _quotes.Remove(key);
    }

    private void OnSessionClosing(NCActiveWorldSessionClosingEvent args)
    {
        if (_operations.Count > 0)
            args.AddPendingWrite(Task.WhenAll(_operations.ToArray()));
    }

    private void OnAppraise(Entity<NCPawnShopComponent> ent, ref NCPawnShopAppraiseMessage args)
        => Appraise(ent, args.Actor);

    private void OnSell(Entity<NCPawnShopComponent> ent, ref NCPawnShopSellMessage args)
        => TrySell(ent, args.Actor, args.QuoteId);

    private bool CanInteract(EntityUid merchant, EntityUid user)
        => !TerminatingOrDeleted(merchant) && !TerminatingOrDeleted(user) &&
           _ui.IsUiOpen(merchant, NCPawnShopUiKey.Key, user) &&
           _blocker.CanConsciouslyPerformAction(user) &&
           _interaction.InRangeUnobstructed(user, merchant, range: 2f);

    private bool TryGetCharacter(EntityUid user, out NCCharacterId character)
    {
        character = default;
        return _players.TryGetSessionByEntity(user, out var session) &&
               _preferences.TryGetSelectedNCCharacterId(session.UserId, out character);
    }

    private EntityUid? FindPallet(Entity<NCPawnShopComponent> merchant)
    {
        var origin = Transform(merchant.Owner);
        if (origin.GridUid is not { } grid || string.IsNullOrWhiteSpace(merchant.Comp.LinkId) ||
            !float.IsFinite(merchant.Comp.MaximumPalletDistance) || merchant.Comp.MaximumPalletDistance <= 0)
            return null;
        EntityUid? result = null;
        var query = EntityQueryEnumerator<NCPawnPalletComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var pallet, out var xform))
        {
            if (pallet.LinkId != merchant.Comp.LinkId || xform.ParentUid != grid || !xform.Anchored ||
                (_transform.GetWorldPosition(uid) - _transform.GetWorldPosition(merchant.Owner)).LengthSquared() >
                merchant.Comp.MaximumPalletDistance * merchant.Comp.MaximumPalletDistance)
                continue;
            // Duplicate mapper links are an error, never a license to collect another customer's goods.
            if (result != null)
                return null;
            result = uid;
        }
        return result;
    }

    private Quote? GetQuote(Entity<NCPawnShopComponent> merchant)
    {
        if (FindPallet(merchant) is not { } pallet || _busyPallets.Contains(pallet))
            return null;
        var entities = new HashSet<EntityUid>();
        _lookup.GetEntitiesIntersecting(pallet, entities, LookupFlags.Dynamic | LookupFlags.Sundries);
        var goods = new List<Goods>();
        long total = 0;
        foreach (var uid in entities.OrderBy(entity => entity.ToString(), StringComparer.Ordinal))
        {
            if (TerminatingOrDeleted(uid) || Transform(uid).Anchored ||
                Transform(uid).ParentUid != Transform(pallet).ParentUid)
                continue;
            var seen = new HashSet<EntityUid>();
            var signature = new StringBuilder();
            if (!TryInspectGoods(uid, seen, signature, 0) ||
                !NCPawnShopPricing.TryGetPayout(_pricing.GetPrice(uid), merchant.Comp.Percentage, out var payout))
                continue;
            if (goods.Count >= MaximumEntities || total > NCPawnShopPricing.MaximumPayout - payout)
                return null;
            goods.Add(new Goods(uid, Name(uid), payout, signature.ToString()));
            total += payout;
        }
        return new Quote(Guid.NewGuid(), pallet, goods, total);
    }

    private bool TryInspectGoods(EntityUid uid, HashSet<EntityUid> seen, StringBuilder signature, int depth)
    {
        if (depth > 32 || seen.Count >= MaximumEntities || !seen.Add(uid) || TerminatingOrDeleted(uid) ||
            HasComp<MobStateComponent>(uid) || HasComp<CargoSellBlacklistComponent>(uid))
            return false;
        var price = _pricing.GetPrice(uid, includeContents: false);
        if (!double.IsFinite(price) || price < 0)
            return false;
        signature.Append(uid).Append(':').Append(price.ToString("R", CultureInfo.InvariantCulture));
        if (TryComp<StackComponent>(uid, out var stack))
            signature.Append(':').Append(stack.Count);
        signature.Append(';');
        if (TryComp<ContainerManagerComponent>(uid, out var manager))
        {
            foreach (var container in manager.Containers.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                signature.Append(container.Key).Append('[');
                foreach (var child in container.Value.ContainedEntities.OrderBy(entity => entity.ToString(), StringComparer.Ordinal))
                    signature.Append(child).Append(',');
                signature.Append(']');
            }
        }
        // Inspect every transform descendant, including attached entities outside ordinary storage slots.
        // The final price still comes from PricingSystem.GetPrice, preserving its custom pricing handlers.
        var children = new List<EntityUid>();
        var enumerator = Transform(uid).ChildEnumerator;
        while (enumerator.MoveNext(out var child))
            children.Add(child);
        foreach (var child in children.OrderBy(entity => entity.ToString(), StringComparer.Ordinal))
        {
            if (!TryInspectGoods(child, seen, signature, depth + 1))
                return false;
        }
        return true;
    }

    private void Appraise(Entity<NCPawnShopComponent> merchant, EntityUid user, string notice = "")
    {
        if (!CanInteract(merchant, user))
            return;
        var quote = GetQuote(merchant);
        _quotes.Remove((merchant.Owner, user));
        var hasAccountIdentity = TryGetCharacter(user, out _);
        if (quote != null)
            _quotes[(merchant.Owner, user)] = quote;
        if (notice.Length == 0)
            notice = quote == null ? "nc-pawn-unavailable" : !hasAccountIdentity ? "nc-pawn-no-account" :
                quote.Goods.Count == 0 ? "nc-pawn-empty" : "nc-pawn-ready";
        _ui.SetUiState(merchant.Owner, NCPawnShopUiKey.Key, new NCPawnShopState(
            merchant.Comp.Percentage, quote?.Total ?? 0,
            quote?.Goods.Select(good => new NCPawnShopListing(good.Name, good.Price)).ToList() ?? [],
            quote?.Id ?? Guid.Empty, notice, hasAccountIdentity && quote is { Total: > 0 }));
    }

    private static bool SameGoods(Quote expected, Quote current)
        => expected.Pallet == current.Pallet && expected.Total == current.Total &&
           expected.Goods.SequenceEqual(current.Goods);

    public bool CanSell(Entity<NCPawnShopComponent> merchant, EntityUid user, Guid quoteId)
        => CanInteract(merchant, user) && TryGetCharacter(user, out _) &&
           _quotes.TryGetValue((merchant.Owner, user), out var quote) && quote.Id == quoteId && quote.Total > 0 &&
           !_busyPallets.Contains(quote.Pallet);

    public bool TrySell(Entity<NCPawnShopComponent> merchant, EntityUid user, Guid quoteId)
    {
        if (!CanSell(merchant, user, quoteId))
            return false;
        var expected = _quotes[(merchant.Owner, user)];
        var current = GetQuote(merchant);
        if (current == null || !SameGoods(expected, current))
        {
            Appraise(merchant, user, "nc-pawn-changed");
            return false;
        }
        if (!TryGetCharacter(user, out var character) || !_busyPallets.Add(expected.Pallet))
            return false;
        _quotes.Remove((merchant.Owner, user));
        _ui.SetUiState(merchant.Owner, NCPawnShopUiKey.Key, new NCPawnShopState(merchant.Comp.Percentage,
            expected.Total, expected.Goods.Select(good => new NCPawnShopListing(good.Name, good.Price)).ToList(),
            Guid.Empty, "nc-pawn-processing", false));
        var operation = SellAsync(merchant.Owner, user, character, expected);
        _operations.Add(operation);
        _ = CompleteOperationAsync(operation);
        return true;
    }

    private async Task CompleteOperationAsync(Task operation)
    {
        try { await operation; }
        finally { await Main(() => _operations.Remove(operation)); }
    }

    private async Task SellAsync(EntityUid merchant, EntityUid user, NCCharacterId character, Quote quote)
    {
        List<(EntityUid Entity, EntityCoordinates Position)>? captured = null;
        var transaction = NCTransactionId.New();
        var retainEscrow = false;
        var posted = false;
        var notice = "nc-pawn-failed";
        try
        {
            var party = NCPartyRef.ForCharacter(character);
            if (await _finance.GetPrimaryAccountAsync(party, true) == null)
            {
                notice = "nc-pawn-no-account";
                return;
            }
            captured = await Main(() => Capture(merchant, user, character, quote));
            if (captured == null)
            {
                notice = "nc-pawn-changed";
                return;
            }
            posted = true;
            NCEconomyMutationResult? result = null;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                result = await _finance.AdjustWithIssuanceAsync(new NCPartyBalanceAdjustmentRequest(
                    Guid.NewGuid(), transaction, party, quote.Total, NCLedgerTransactionKind.Sale,
                    $"pawn-sale:{quote.Id:D}", party, quote.Id, AuditSource: AuditSource));
                if (result.Status != NCEconomyMutationStatus.VersionConflict)
                    break;
            }
            var paid = result?.Status is NCEconomyMutationStatus.Applied or NCEconomyMutationStatus.Duplicate;
            if (!paid && result?.Status == NCEconomyMutationStatus.StorageFailure)
            {
                paid = await _ledger.GetTransactionAsync(transaction, true) != null;
                retainEscrow = !paid;
            }
            if (paid)
            {
                await Main(() => Consume(captured));
                captured = null;
                notice = "nc-pawn-success";
            }
            else if (retainEscrow)
                Log.Error($"Pawn sale {transaction} outcome is unresolved; merchandise remains in {merchant} escrow.");
        }
        catch (Exception exception)
        {
            // Once posting starts, never release the same merchandise against an uncertain bank commit.
            retainEscrow = posted;
            Log.Error($"Pawn sale {transaction} failed (escrow retained={retainEscrow}): {exception}");
        }
        finally
        {
            await Main(() =>
            {
                if (captured != null && !retainEscrow)
                    Restore(merchant, captured);
                if (!retainEscrow)
                    _busyPallets.Remove(quote.Pallet);
                if (TryComp<NCPawnShopComponent>(merchant, out var component))
                    Appraise((merchant, component), user, retainEscrow ? "nc-pawn-pending" : notice);
            });
        }
    }

    private List<(EntityUid Entity, EntityCoordinates Position)>? Capture(EntityUid merchant, EntityUid user,
        NCCharacterId character, Quote quote)
    {
        if (!CanInteract(merchant, user) || !TryGetCharacter(user, out var currentCharacter) || character != currentCharacter ||
            !TryComp<NCPawnShopComponent>(merchant, out var component))
            return null;
        // Temporarily lift our own lock to recompute exactly what is still physically on this pallet.
        _busyPallets.Remove(quote.Pallet);
        var current = GetQuote((merchant, component));
        _busyPallets.Add(quote.Pallet);
        if (current == null || !SameGoods(quote, current))
            return null;
        var escrow = _containers.EnsureContainer<Container>(merchant, EscrowId);
        var captured = new List<(EntityUid Entity, EntityCoordinates Position)>();
        foreach (var good in quote.Goods)
        {
            var position = Transform(good.Entity).Coordinates;
            if (!_containers.Insert(good.Entity, escrow, force: true))
            {
                Restore(merchant, captured);
                return null;
            }
            captured.Add((good.Entity, position));
        }
        return captured;
    }

    private void Restore(EntityUid merchant, List<(EntityUid Entity, EntityCoordinates Position)> captured)
    {
        if (!_containers.TryGetContainer(merchant, EscrowId, out var escrow))
            return;
        foreach (var (uid, position) in captured)
        {
            if (!TerminatingOrDeleted(uid))
                _containers.Remove(uid, escrow, force: true, destination: position);
        }
    }

    private void Consume(List<(EntityUid Entity, EntityCoordinates Position)> captured)
    {
        foreach (var (uid, _) in captured)
        {
            if (!TerminatingOrDeleted(uid))
                Del(uid);
        }
    }

    private Task<T> Main<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tasks.RunOnMainThread(() =>
        {
            try { completion.SetResult(action()); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        return completion.Task;
    }

    private Task Main(Action action) => Main(() => { action(); return true; });

    private sealed record Goods(EntityUid Entity, string Name, long Price, string Signature);
    private sealed record Quote(Guid Id, EntityUid Pallet, List<Goods> Goods, long Total);
}
