// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Threading.Tasks;
using Content.Server._NC.Bank;

namespace Content.Server._NC.Housing;

/// <summary>Duplicate cards, changing the locks and reselling a lease to another character.</summary>
public sealed partial class NCPropertySystem
{
    private bool TryGetPayer(EntityUid user, out NCBankAccountRecord account, out string error)
    {
        error = string.Empty;
        if (!Ready || !_bank.TryGetCharacterAccount(user, out account) || account.ProfileId == null)
        {
            account = default!;
            error = "nc-property-error-no-account";
            return false;
        }

        return true;
    }

    /// <summary>The user's own lease of a known property, free of a resale payment in flight.</summary>
    private bool TryGetOwnLease(
        NCBankAccountRecord account,
        string propertyId,
        out NCPropertyRecord property,
        out NCPropertyLease lease,
        out string error)
    {
        lease = default!;
        error = string.Empty;
        if (!Properties.TryGetValue(propertyId, out property!))
        {
            error = "nc-property-error-unknown";
            return false;
        }

        lease = GetLease(propertyId);
        if (_pendingSales.Contains(propertyId))
        {
            error = "nc-property-error-busy";
            return false;
        }

        if (lease.Tenant == null || lease.Tenant != account.ProfileId)
        {
            error = "nc-property-error-not-yours";
            return false;
        }

        return true;
    }

    public bool TryDuplicate(EntityUid user, string propertyId, int door, out string error)
    {
        return TryGetPayer(user, out var account, out error) && TryDuplicate(user, account, propertyId, door, out error);
    }

    /// <summary>Cuts one more card for a door of the payer's lease.</summary>
    public bool TryDuplicate(EntityUid user, NCBankAccountRecord account, string propertyId, int door, out string error)
    {
        if (!TryGetOwnLease(account, propertyId, out var property, out var lease, out error))
            return false;

        if (door < 0 || door >= property.Doors.Count)
        {
            error = "nc-property-error-door";
            return false;
        }

        if (!_bank.TryPayRent(account, Config.CardPrice, propertyId, user))
        {
            error = "nc-property-error-funds";
            return false;
        }

        IssueCard(user, property, lease, door);
        return true;
    }

    public bool TryRekey(EntityUid user, string propertyId, out string error)
    {
        return TryGetPayer(user, out var account, out error) && TryRekey(user, account, propertyId, out error);
    }

    /// <summary>Changes the locks of the payer's lease: every card out there dies and a fresh set is handed over.</summary>
    public bool TryRekey(EntityUid user, NCBankAccountRecord account, string propertyId, out string error)
    {
        if (!TryGetOwnLease(account, propertyId, out var property, out var lease, out error))
            return false;

        if (!_bank.TryPayRent(account, Config.RekeyPrice, propertyId, user))
        {
            error = "nc-property-error-funds";
            return false;
        }

        lease.KeyVersion++;
        Save(lease);
        IssueCards(user, property, lease);
        return true;
    }

    public bool TryListForSale(EntityUid user, string propertyId, long price, string? buyer, out string error)
    {
        return TryGetPayer(user, out var account, out error) && TryListForSale(account, propertyId, price, buyer, out error);
    }

    /// <summary>
    /// Offers the seller's lease for resale. The money goes to the seller's account; a named buyer account
    /// reserves the offer for that account alone.
    /// </summary>
    public bool TryListForSale(NCBankAccountRecord seller, string propertyId, long price, string? buyer, out string error)
    {
        if (!TryGetOwnLease(seller, propertyId, out _, out var lease, out error))
            return false;

        if (price <= 0 || price > _bank.Configuration.MaximumOperation)
        {
            error = "nc-property-error-price";
            return false;
        }

        var reserved = string.IsNullOrWhiteSpace(buyer) ? null : buyer.Trim().ToUpperInvariant();
        if (reserved == seller.Number)
        {
            error = "nc-property-error-own-account";
            return false;
        }

        lease.ResalePrice = price;
        lease.ResaleSeller = seller.Number;
        lease.ResaleBuyer = reserved;
        Save(lease);
        return true;
    }

    public bool TryUnlist(EntityUid user, string propertyId, out string error)
    {
        if (!TryGetPayer(user, out var account, out error) ||
            !TryGetOwnLease(account, propertyId, out _, out var lease, out error))
        {
            return false;
        }

        ClearResale(lease);
        Save(lease);
        return true;
    }

    public async Task<(bool Success, string Error)> TryBuyAsync(EntityUid user, string propertyId)
    {
        return TryGetPayer(user, out var account, out var error)
            ? await TryBuyAsync(user, account, propertyId)
            : (false, error);
    }

    /// <summary>
    /// Buys a lease offered for resale: the buyer pays the asking price to the seller by bank transfer (the
    /// buyer's bank fee on top), the locks change and the buyer gets the cards and the time already paid.
    /// </summary>
    public async Task<(bool Success, string Error)> TryBuyAsync(EntityUid user, NCBankAccountRecord buyer, string propertyId)
    {
        if (!Properties.TryGetValue(propertyId, out var property))
            return (false, "nc-property-error-unknown");

        var lease = GetLease(propertyId);
        if (_pendingSales.Contains(propertyId))
            return (false, "nc-property-error-busy");

        if (lease.Tenant is not { } seller || lease.ResalePrice is not { } price || lease.ResaleSeller is not { } sellerNumber)
            return (false, "nc-property-error-not-for-sale");

        if (buyer.ProfileId == null || buyer.ProfileId == seller)
            return (false, "nc-property-error-own-lease");

        if (lease.ResaleBuyer != null && lease.ResaleBuyer != buyer.Number)
            return (false, "nc-property-error-reserved");

        _pendingSales.Add(propertyId);
        NCBankTransferResult result;
        try
        {
            result = await _bank.TryTransferAsync(buyer, sellerNumber, price, user);
        }
        finally
        {
            _pendingSales.Remove(propertyId);
        }

        if (result != NCBankTransferResult.Success)
        {
            return (false, result switch
            {
                NCBankTransferResult.InsufficientFunds => "nc-property-error-funds",
                NCBankTransferResult.AccountNotFound => "nc-property-error-seller-gone",
                _ => "nc-property-error-price",
            });
        }

        lease.Tenant = buyer.ProfileId;
        lease.KeyVersion++;
        lease.Warned = false;
        ClearResale(lease);
        Save(lease);
        if (!Deleted(user))
            IssueCards(user, property, lease);

        Notify(seller, Loc.GetString("nc-property-sold", ("property", property.Name), ("price", price)));
        return (true, string.Empty);
    }
}
