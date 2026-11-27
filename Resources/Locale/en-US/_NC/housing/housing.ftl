# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

ent-NCPropertyMarker = property marker
    .desc = Mapping marker for a rentable property: set its id, name and hourly rent.
ent-NCRealEstateTerminal = real estate agency terminal
    .desc = Apartments, garages and business premises for rent. Prepay, get the key cards, move in.
ent-NCPropertyKeyCard = key card
    .desc = A plastic key card for a rented door.

nc-property-card-name = key card: {$property}, door {$door}/{$doors}
nc-property-card-examine = Opens a door of [color=yellow]{$property}[/color] while the lease it was cut for lasts.
nc-property-door-locked = Locked. You need the key card for this door.
nc-property-expiring = The rent on {$property} runs out in 15 minutes. Extend it at the real estate agency.
nc-property-evicted = The rent on {$property} ran out. The locks have been changed.

nc-property-rented = Lease signed. Your key cards are in your hands.
nc-property-extended = Lease extended.
nc-property-cancelled = Lease ended. The locks have been changed.
nc-property-error-closed = The agency is offline right now.
nc-property-error-unknown = That property is not on the market.
nc-property-error-term = The agency does not offer that term.
nc-property-error-no-account = You need a bank account to rent.
nc-property-error-taken = Someone has already rented it.
nc-property-error-funds = Not enough money on your account.
nc-property-error-not-yours = That lease is not yours.

nc-real-estate-title = Real estate agency
nc-real-estate-balance = Account:
nc-real-estate-money = {$amount} €$
nc-real-estate-mine-only = My leases only
nc-real-estate-hint = Rent is prepaid in hours of city time. When it runs out, the locks change; what you left inside stays.
nc-real-estate-closed = The agency is offline.
nc-real-estate-no-account = You need a bank account to rent.
nc-real-estate-empty = Nothing to show.
nc-real-estate-place = {$district}, {$building}, floor {$floor}
nc-real-estate-unknown = unknown
nc-real-estate-details = {$area} tiles · {$doors ->
    [one] 1 door
   *[other] {$doors} doors
} · {$rent} €$/h
nc-real-estate-status-vacant = Vacant
nc-real-estate-status-rented = Rented
nc-real-estate-status-mine = Your lease · {$time} left
nc-real-estate-rent = Rent for:
nc-real-estate-extend = Extend by:
nc-real-estate-term = {$hours} h · {$cost} €$
nc-real-estate-cancel = End the lease

nc-property-duplicated = Duplicate card cut.
nc-property-rekeyed = Locks changed. Every old card is dead; here is the new set.
nc-property-listed = Lease offered for resale.
nc-property-unlisted = Lease taken off the market.
nc-property-bought = Lease bought. The locks have been changed; your key cards are in your hands.
nc-property-sold = Your lease of {$property} was bought for {$price} €$. The money is on your account.
nc-property-error-busy = A payment for this property is going through. Try again in a moment.
nc-property-error-door = That door does not belong to the property.
nc-property-error-price = That price will not do.
nc-property-error-own-account = You cannot reserve your own lease for your own account.
nc-property-error-not-for-sale = That lease is not for sale.
nc-property-error-own-lease = That lease is already yours.
nc-property-error-reserved = That lease is reserved for another buyer.
nc-property-error-seller-gone = The seller's account no longer exists.

nc-real-estate-status-for-sale = Lease for resale: {$price} €$ · {$time} paid ahead
nc-real-estate-buy = Buy the lease · {$price} €$ + bank fee
nc-real-estate-reserved = Reserved for another buyer
nc-real-estate-door = Door {$door}
nc-real-estate-duplicate = Duplicate card · {$price} €$
nc-real-estate-rekey = Change the locks · {$price} €$
nc-real-estate-listed = On sale for {$price} €$
nc-real-estate-listed-for = On sale for {$price} €$ to {$buyer}
nc-real-estate-unlist = Take off the market
nc-real-estate-price-placeholder = Price
nc-real-estate-buyer-placeholder = Buyer account (optional)
nc-real-estate-list = Offer for resale
