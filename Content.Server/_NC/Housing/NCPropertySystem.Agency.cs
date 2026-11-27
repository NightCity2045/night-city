// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Linq;
using Content.Shared._NC.Housing;
using Robust.Server.GameObjects;

namespace Content.Server._NC.Housing;

/// <summary>The real estate agency terminal: list of properties, renting, extending and giving them up.</summary>
public sealed partial class NCPropertySystem
{
    [Dependency] private UserInterfaceSystem _ui = default!;

    private void InitializeAgency()
    {
        SubscribeLocalEvent<NCRealEstateTerminalComponent, BoundUIOpenedEvent>((ent, ref _) => UpdateAgency(ent));
        SubscribeLocalEvent<NCRealEstateTerminalComponent, NCRealEstateRentMessage>(OnRent);
        SubscribeLocalEvent<NCRealEstateTerminalComponent, NCRealEstateExtendMessage>(OnExtend);
        SubscribeLocalEvent<NCRealEstateTerminalComponent, NCRealEstateCancelMessage>(OnCancel);
        SubscribeLocalEvent<NCRealEstateTerminalComponent, NCRealEstateDuplicateMessage>(OnDuplicate);
        SubscribeLocalEvent<NCRealEstateTerminalComponent, NCRealEstateRekeyMessage>(OnRekey);
        SubscribeLocalEvent<NCRealEstateTerminalComponent, NCRealEstateListMessage>(OnList);
        SubscribeLocalEvent<NCRealEstateTerminalComponent, NCRealEstateUnlistMessage>(OnUnlist);
        SubscribeLocalEvent<NCRealEstateTerminalComponent, NCRealEstateBuyMessage>(OnBuy);
        SubscribeLocalEvent<NCRealEstateTerminalComponent, NCRealEstateSetHomeMessage>(OnSetHome);
    }

    private void OnSetHome(Entity<NCRealEstateTerminalComponent> ent, ref NCRealEstateSetHomeMessage args)
    {
        Report(ent, args.Actor, TrySetHome(args.Actor, args.Property, args.Home, out var error),
            args.Home ? "nc-property-home-set" : "nc-property-home-cleared", error);
    }

    private void OnDuplicate(Entity<NCRealEstateTerminalComponent> ent, ref NCRealEstateDuplicateMessage args)
    {
        Report(ent, args.Actor, TryDuplicate(args.Actor, args.Property, args.Door, out var error), "nc-property-duplicated", error);
    }

    private void OnRekey(Entity<NCRealEstateTerminalComponent> ent, ref NCRealEstateRekeyMessage args)
    {
        Report(ent, args.Actor, TryRekey(args.Actor, args.Property, out var error), "nc-property-rekeyed", error);
    }

    private void OnList(Entity<NCRealEstateTerminalComponent> ent, ref NCRealEstateListMessage args)
    {
        Report(ent, args.Actor, TryListForSale(args.Actor, args.Property, args.Price, args.Buyer, out var error), "nc-property-listed", error);
    }

    private void OnUnlist(Entity<NCRealEstateTerminalComponent> ent, ref NCRealEstateUnlistMessage args)
    {
        Report(ent, args.Actor, TryUnlist(args.Actor, args.Property, out var error), "nc-property-unlisted", error);
    }

    private void OnBuy(Entity<NCRealEstateTerminalComponent> ent, ref NCRealEstateBuyMessage args)
    {
        Buy(ent, args.Actor, args.Property);
    }

    private async void Buy(Entity<NCRealEstateTerminalComponent> ent, EntityUid user, string property)
    {
        var (success, error) = await TryBuyAsync(user, property);
        if (!Deleted(ent) && !Deleted(user))
            Report(ent, user, success, "nc-property-bought", error);
    }

    private void OnRent(Entity<NCRealEstateTerminalComponent> ent, ref NCRealEstateRentMessage args)
    {
        Report(ent, args.Actor, TryRent(args.Actor, args.Property, args.Hours, out var error), "nc-property-rented", error);
    }

    private void OnExtend(Entity<NCRealEstateTerminalComponent> ent, ref NCRealEstateExtendMessage args)
    {
        Report(ent, args.Actor, TryExtend(args.Actor, args.Property, args.Hours, out var error), "nc-property-extended", error);
    }

    private void OnCancel(Entity<NCRealEstateTerminalComponent> ent, ref NCRealEstateCancelMessage args)
    {
        Report(ent, args.Actor, TryCancel(args.Actor, args.Property, out var error), "nc-property-cancelled", error);
    }

    private void Report(Entity<NCRealEstateTerminalComponent> ent, EntityUid user, bool success, string done, string error)
    {
        _popup.PopupEntity(Loc.GetString(success ? done : error), ent, user);
        UpdateAgencies();
    }

    /// <summary>Refreshes every open agency terminal; a lease change shows on all of them.</summary>
    private void UpdateAgencies()
    {
        var query = EntityQueryEnumerator<NCRealEstateTerminalComponent>();
        while (query.MoveNext(out var uid, out var terminal))
        {
            if (_ui.IsUiOpen(uid, NCRealEstateUiKey.Key))
                UpdateAgency((uid, terminal));
        }
    }

    private void UpdateAgency(Entity<NCRealEstateTerminalComponent> ent)
    {
        foreach (var actor in _ui.GetActors(ent.Owner, NCRealEstateUiKey.Key).ToList())
        {
            var hasAccount = _bank.TryGetCharacterAccount(actor, out var account) && account.ProfileId != null;
            var listings = new List<NCPropertyListing>();
            if (Ready)
            {
                foreach (var property in Properties.Values.OrderBy(p => p.District).ThenBy(p => p.Building).ThenBy(p => p.Name))
                {
                    if (property.RentPerHour <= 0)
                        continue;

                    var lease = GetLease(property.Id);
                    var status = lease.Tenant == null ? NCPropertyStatus.Vacant :
                        hasAccount && lease.Tenant == account.ProfileId ? NCPropertyStatus.Mine :
                        lease.ResalePrice != null ? NCPropertyStatus.ForSale :
                        NCPropertyStatus.Rented;
                    var reserved = status == NCPropertyStatus.ForSale && lease.ResaleBuyer != null &&
                                   (!hasAccount || lease.ResaleBuyer != account.Number);
                    listings.Add(new NCPropertyListing(
                        property.Id,
                        property.Name,
                        property.District,
                        property.Building,
                        property.Floor,
                        property.Tiles.Count,
                        property.Doors.Count,
                        property.RentPerHour,
                        status,
                        status is NCPropertyStatus.Mine or NCPropertyStatus.ForSale ? GetRemaining(lease) : TimeSpan.Zero,
                        lease.ResalePrice ?? 0,
                        reserved,
                        status == NCPropertyStatus.Mine ? lease.ResaleBuyer ?? string.Empty : string.Empty,
                        status == NCPropertyStatus.Mine && lease.Home));
                }
            }

            _ui.SetUiState(ent.Owner, NCRealEstateUiKey.Key, new NCRealEstateUiState(
                Ready,
                hasAccount,
                hasAccount ? account.Balance : 0,
                Config.Terms.ToList(),
                Config.CardPrice,
                Config.RekeyPrice,
                listings));
        }
    }
}
