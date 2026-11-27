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
