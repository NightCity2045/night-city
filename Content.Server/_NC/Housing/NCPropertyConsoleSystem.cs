// SPDX-FileCopyrightText: 2026 Astro
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
// SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Content.Server._NC.LivingCity.Economy;
using Content.Server._NC.LivingCity.Time;
using Content.Server.Preferences.Managers;
using Content.Shared._NC.City.ZoneTemplates.Prototypes;
using Content.Shared._NC.City.Zones;
using Content.Shared._NC.Housing;
using Content.Shared._NC.Housing.Components;
using Content.Shared._NC.Housing.Prototypes;
using Content.Shared._NC.Housing.UI;
using Content.Shared._NC.Identity;
using Content.Shared._NC.Persistence.Identity;
using Robust.Server.GameObjects;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;

namespace Content.Server._NC.Housing;

/// <summary>
/// Server-authoritative real-estate terminal. Client flags are presentation only; every mutation is revalidated here.
/// </summary>
public sealed partial class NCPropertyConsoleSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private ISharedPlayerManager _players = default!;
    [Dependency] private IServerPreferencesManager _preferences = default!;
    [Dependency] private NCPartyFinanceAdapterSystem _finance = default!;
    [Dependency] private NCActiveWorldTimeSystem _clock = default!;
    [Dependency] private NCApartmentSystem _apartments = default!;
    [Dependency] private City.Zones.NCZoneSystem _zones = default!;
    [Dependency] private UserInterfaceSystem _ui = default!;

    private readonly Dictionary<(EntityUid Console, EntityUid User), PropertyView> _views = [];
    private readonly HashSet<(EntityUid Console, EntityUid User)> _processing = [];

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<NCPropertyConsoleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<NCPropertyConsoleComponent, BoundUIClosedEvent>(OnUiClosed);
        SubscribeLocalEvent<NCPropertyConsoleComponent, NCPropertyConsoleSearchMessage>(OnSearch);
        SubscribeLocalEvent<NCPropertyConsoleComponent, NCPropertyConsolePageMessage>(OnPage);
        SubscribeLocalEvent<NCPropertyConsoleComponent, NCPropertyConsoleSelectMessage>(OnSelect);
        SubscribeLocalEvent<NCPropertyConsoleComponent, NCPropertyConsoleRentMessage>(OnRent);
        SubscribeLocalEvent<NCPropertyConsoleComponent, NCPropertyConsoleEndLeaseMessage>(OnEndLease);
        SubscribeLocalEvent<NCPropertyConsoleComponent, NCPropertyConsoleInvokeInteriorMessage>(OnInvokeInterior);
        SubscribeLocalEvent<NCPropertyChangedEvent>(OnPropertyChanged);
    }

    private async void OnUiOpened(EntityUid uid, NCPropertyConsoleComponent component, BoundUIOpenedEvent args)
    {
        if (args.Actor is not { Valid: true } user)
            return;

        _views[(uid, user)] = new PropertyView();
        if (TryGetCharacterId(user, out var characterId))
            await _apartments.ProcessCharacterRentAsync(characterId);
        await SendViewAsync(uid, component, user);
    }

    private void OnUiClosed(EntityUid uid, NCPropertyConsoleComponent component, BoundUIClosedEvent args)
    {
        if (args.Actor is not { Valid: true } user)
            return;

        _views.Remove((uid, user));
        _processing.Remove((uid, user));
    }

    private async void OnSearch(EntityUid uid, NCPropertyConsoleComponent component, NCPropertyConsoleSearchMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view))
            return;

        var query = args.Query.Trim();
        if (query.Length > component.MaximumSearchLength)
            return;

        view.Query = query;
        view.PageIndex = 0;
        view.SelectedListingId = null;
        view.Notice = string.Empty;
        await SendViewAsync(uid, component, user);
    }

    private async void OnPage(EntityUid uid, NCPropertyConsoleComponent component, NCPropertyConsolePageMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view))
            return;

        view.PageIndex = Math.Max(0, args.PageIndex);
        view.SelectedListingId = null;
        view.Notice = string.Empty;
        await SendViewAsync(uid, component, user);
    }

    private async void OnSelect(EntityUid uid, NCPropertyConsoleComponent component, NCPropertyConsoleSelectMessage args)
    {
        if (!TryGetView(uid, args.Actor, out var user, out var view) ||
            !view.VisibleListings.Contains(args.ListingId))
            return;

        view.SelectedListingId = args.ListingId;
        view.Notice = string.Empty;
        await SendViewAsync(uid, component, user);
    }

    private async void OnRent(EntityUid uid, NCPropertyConsoleComponent component, NCPropertyConsoleRentMessage args)
    {
        await ExecuteRentAsync(uid, component, args.Actor, args.ListingId, args.TemplateId);
    }

    private async void OnEndLease(EntityUid uid, NCPropertyConsoleComponent component, NCPropertyConsoleEndLeaseMessage args)
    {
        await ExecuteEndLeaseAsync(uid, component, args.Actor, args.ListingId);
    }

    private async void OnInvokeInterior(
        EntityUid uid,
        NCPropertyConsoleComponent component,
        NCPropertyConsoleInvokeInteriorMessage args)
    {
        if (!TryGetOperation(uid, args.Actor, args.ListingId,
                out var user, out var view, out var characterId, out var listing) ||
            !_processing.Add((uid, user)))
            return;

        try
        {
            if (_apartments.IsInteriorActiveOrPending(listing.ApartmentId))
            {
                view.Notice = "nc-property-console-result-interior-active";
            }
            else if (_apartments.TryInvokeInterior(listing.ApartmentId, characterId, out _, out _))
            {
                view.Notice = "nc-property-console-result-interior-invoked";
            }
            else
            {
                view.Notice = "nc-property-console-result-interior-failed";
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Property console failed to invoke an interior at {uid}: {exception}");
            view.Notice = "nc-property-console-result-failed";
        }
        finally
        {
            _processing.Remove((uid, user));
            await SendViewAsync(uid, component, user);
        }
    }

    private async Task ExecuteRentAsync(
        EntityUid console,
        NCPropertyConsoleComponent component,
        EntityUid? actor,
        string listingId,
        string templateId)
    {
        if (!TryGetOperation(console, actor, listingId,
                out var user, out var view, out var characterId, out var listing) ||
            !_processing.Add((console, user)))
            return;

        try
        {
            var selected = new ProtoId<NCZoneTemplatePrototype>(templateId);
            if (!listing.Interiors.Any(value => value.Template == selected))
            {
                view.Notice = "nc-property-console-result-invalid-interior";
            }
            else
            {
                var result = await _apartments.TryRentAsync(
                    listing.ApartmentId,
                    characterId,
                    selected,
                    user);
                view.Notice = MutationNotice(result.Result);
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Property console failed to rent at {console}: {exception}");
            view.Notice = "nc-property-console-result-failed";
        }
        finally
        {
            _processing.Remove((console, user));
            await RefreshOpenViewsAsync();
        }
    }

    private async Task ExecuteEndLeaseAsync(
        EntityUid console,
        NCPropertyConsoleComponent component,
        EntityUid? actor,
        string listingId)
    {
        if (!TryGetOperation(console, actor, listingId,
                out var user, out var view, out var characterId, out var listing) ||
            !_processing.Add((console, user)))
            return;

        try
        {
            // The database repeats this tenancy check so a forged BUI message cannot terminate another lease.
            if (!_apartments.TryGetProperty(listing.ApartmentId, out var property) ||
                !property.Snapshot.Occupants.Any(value =>
                    value.Kind == NCPropertyOccupancyKind.Tenancy &&
                    value.Occupant == NCPartyRef.ForCharacter(characterId)))
            {
                view.Notice = "nc-property-console-result-not-authorized";
            }
            else
            {
                var result = await _apartments.TryReleaseAsync(
                    listing.ApartmentId,
                    characterId,
                    NCPropertyReleaseReason.VoluntaryMoveOut);
                view.Notice = EndLeaseNotice(result.Result);
            }
        }
        catch (Exception exception)
        {
            Log.Error($"Property console failed to end a lease at {console}: {exception}");
            view.Notice = "nc-property-console-result-failed";
        }
        finally
        {
            _processing.Remove((console, user));
            await RefreshOpenViewsAsync();
        }
    }

    private async Task SendViewAsync(EntityUid console, NCPropertyConsoleComponent component, EntityUid user)
    {
        if (Deleted(console) || Deleted(user) || !_views.TryGetValue((console, user), out var view))
            return;

        if (!TryGetCharacterId(user, out var characterId))
        {
            _ui.ServerSendUiMessage(console, NCPropertyConsoleUiKey.Key,
                new NCPropertyConsoleUpdateMessage([], null, 0, 1, 0, 0, false,
                    "nc-property-console-result-no-character"), user);
            return;
        }

        var account = await _finance.GetPrimaryAccountAsync(NCPartyRef.ForCharacter(characterId));
        var balance = account?.Snapshot.Balance ?? 0;
        _clock.TryGetCurrentTime(out _, out var now);
        var listings = BuildVisibleListings(view.Query);
        var pageSize = Math.Max(1, component.PageSize);
        var pageCount = Math.Max(1, (listings.Count + pageSize - 1) / pageSize);
        view.PageIndex = Math.Clamp(view.PageIndex, 0, pageCount - 1);
        var page = listings.Skip(view.PageIndex * pageSize).Take(pageSize).ToList();

        view.VisibleListings.Clear();
        foreach (var entry in page)
            view.VisibleListings.Add(entry.Listing.ID);
        if (view.SelectedListingId == null || !view.VisibleListings.Contains(view.SelectedListingId))
            view.SelectedListingId = page.FirstOrDefault()?.Listing.ID;

        var summaries = new List<NCPropertyConsoleListingSummary>(page.Count);
        foreach (var entry in page)
        {
            _apartments.TryGetProperty(entry.Listing.ApartmentId, out var property);
            var snapshot = property?.Snapshot;
            var status = snapshot?.Status ?? NCPropertyStatus.Unavailable;
            var party = NCPartyRef.ForCharacter(characterId);
            var isTenant = snapshot?.Occupants.Any(tenant =>
                tenant.Kind == NCPropertyOccupancyKind.Tenancy && tenant.Occupant == party &&
                (tenant.ValidUntil is not { } expiry || expiry > now)) == true;
            var interiors = new List<NCPropertyConsoleInteriorOfferSummary>();
            foreach (var offer in entry.Listing.Interiors)
            {
                if (!_prototypes.TryIndex(offer.Template, out NCZoneTemplatePrototype? template) ||
                    template.TargetZoneId != entry.Listing.ApartmentId.ZoneId ||
                    !_prototypes.TryIndex(offer.RentPolicy, out NCRentPolicyPrototype? rentPolicy))
                    continue;

                interiors.Add(new NCPropertyConsoleInteriorOfferSummary(
                    offer.Template.Id,
                    rentPolicy.RentAmount,
                    (long) rentPolicy.BillingInterval.TotalSeconds,
                    (long) rentPolicy.GracePeriod.TotalSeconds));
            }

            var interiorActive = _apartments.IsInteriorActiveOrPending(entry.Listing.ApartmentId);
            var currentRent = snapshot != null &&
                              _prototypes.TryIndex<NCRentPolicyPrototype>(snapshot.RentPolicyId, out var policy)
                ? policy
                : null;
            var nextRentDue = snapshot?.NextRentDueAt is { } dueAt
                ? Math.Max(0, (dueAt.Ticks - now.Ticks) / TimeSpan.TicksPerSecond)
                : (long?) null;

            summaries.Add(new NCPropertyConsoleListingSummary(
                entry.Listing.ID,
                entry.Listing.ApartmentId,
                entry.Apartment.Name,
                entry.Building.Name,
                status,
                currentRent?.RentAmount ?? 0,
                (long) (currentRent?.BillingInterval.TotalSeconds ?? 0),
                (long) (currentRent?.GracePeriod.TotalSeconds ?? 0),
                nextRentDue,
                isTenant,
                account != null && status == NCPropertyStatus.Available &&
                    interiors.Any(offer => balance >= offer.RentAmount),
                isTenant,
                isTenant && snapshot?.CurrentTemplateId != null && !interiorActive &&
                    _apartments.AreInteriorSnapshotsReady,
                interiorActive,
                snapshot?.CurrentTemplateId,
                interiors));
        }

        _ui.ServerSendUiMessage(console, NCPropertyConsoleUiKey.Key, new NCPropertyConsoleUpdateMessage(
            summaries,
            view.SelectedListingId,
            view.PageIndex,
            pageCount,
            listings.Count,
            balance,
            account != null,
            view.Notice), user);
    }

    private List<ListingView> BuildVisibleListings(string query)
    {
        var result = new List<ListingView>();
        foreach (var listing in _prototypes.EnumeratePrototypes<NCPropertyListingPrototype>())
        {
            if (!_zones.TryGetApartment(listing.ApartmentId, out var apartment) ||
                !_zones.TryGetBuilding(new NCBuildingId(apartment.Parent), out var building))
                continue;

            if (query.Length > 0 &&
                !listing.ID.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !apartment.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !building.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(new ListingView(listing, apartment, building));
        }

        return result
            .OrderBy(entry => entry.Building.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Apartment.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Listing.ID, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool TryGetOperation(
        EntityUid console,
        EntityUid? actor,
        string listingId,
        out EntityUid user,
        out PropertyView view,
        out NCCharacterId characterId,
        out NCPropertyListingPrototype listing)
    {
        characterId = default;
        listing = default!;
        if (!TryGetView(console, actor, out user, out view) ||
            view.SelectedListingId != listingId ||
            !view.VisibleListings.Contains(listingId) ||
            !TryGetCharacterId(user, out characterId) ||
            !_prototypes.TryIndex<NCPropertyListingPrototype>(listingId, out var found))
            return false;

        listing = found;
        return true;
    }

    private bool TryGetView(EntityUid console, EntityUid? actor, out EntityUid user, out PropertyView view)
    {
        user = actor ?? EntityUid.Invalid;
        view = null!;
        if (!user.Valid || Deleted(user) || !_views.TryGetValue((console, user), out var found))
            return false;

        view = found;
        return true;
    }

    private bool TryGetCharacterId(EntityUid user, out NCCharacterId characterId)
    {
        characterId = default;
        return _players.TryGetSessionByEntity(user, out var session) &&
               _preferences.TryGetSelectedNCCharacterId(session.UserId, out characterId);
    }

    private void OnPropertyChanged(ref NCPropertyChangedEvent args)
    {
        // The active terminal operation performs one final refresh with its result notice.
        // Skipping this intermediate refresh also prevents an older blank state arriving afterward.
        if (_processing.Count > 0)
            return;

        _ = RefreshOpenViewsAsync();
    }

    private async Task RefreshOpenViewsAsync()
    {
        foreach (var key in _views.Keys.ToArray())
        {
            if (Deleted(key.Console) || Deleted(key.User) ||
                !TryComp<NCPropertyConsoleComponent>(key.Console, out var component))
            {
                _views.Remove(key);
                _processing.Remove(key);
                continue;
            }

            await SendViewAsync(key.Console, component, key.User);
        }
    }

    private static string MutationNotice(NCPropertyMutationResult result)
    {
        if (result == NCPropertyMutationResult.Success)
            return "nc-property-console-result-rented";

        return result switch
        {
            NCPropertyMutationResult.BankAccountNotFound => "nc-property-console-result-no-account",
            NCPropertyMutationResult.InsufficientFunds => "nc-property-console-result-insufficient-funds",
            NCPropertyMutationResult.NotAvailable => "nc-property-console-result-not-available",
            NCPropertyMutationResult.NotAuthorized => "nc-property-console-result-not-authorized",
            NCPropertyMutationResult.Conflict => "nc-property-console-result-conflict",
            _ => "nc-property-console-result-failed",
        };
    }

    private static string EndLeaseNotice(NCPropertyMutationResult result)
    {
        if (result == NCPropertyMutationResult.Success)
            return "nc-property-console-result-lease-ended";

        return result switch
        {
            NCPropertyMutationResult.NotAuthorized => "nc-property-console-result-not-authorized",
            NCPropertyMutationResult.Conflict => "nc-property-console-result-move-out-blocked",
            _ => "nc-property-console-result-failed",
        };
    }

    private sealed class PropertyView
    {
        public readonly HashSet<string> VisibleListings = [];
        public string Query = string.Empty;
        public string? SelectedListingId;
        public string Notice = string.Empty;
        public int PageIndex;
    }

    private sealed record ListingView(
        NCPropertyListingPrototype Listing,
        NCZoneInfo Apartment,
        NCZoneInfo Building);
}
