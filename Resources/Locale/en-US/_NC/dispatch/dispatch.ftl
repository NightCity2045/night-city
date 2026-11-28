# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

## Services and programs

nc-emergency-service-ncpd = NCPD
nc-emergency-service-trauma = Trauma Team
nc-emergency-program-name = Emergency
nc-dispatch-program-name = Dispatch

## Incidents

nc-dispatch-notification-header = Dispatch: {$service}
nc-dispatch-notification-new = #{$id} {$kind}: {$location}, floor {$z}. {$message}
nc-dispatch-kind-call = Call
nc-dispatch-kind-biomonitor = Biomonitor
nc-dispatch-kind-shooting = Shooting
nc-dispatch-location-building = {$building}, {$district}
nc-dispatch-location-unknown = unknown location
nc-dispatch-map-marker = #{$id} {$kind}
nc-dispatch-message-empty = no details
nc-dispatch-caller-sensor = gunshot sensor
nc-dispatch-shooting-message = Gunfire detected.
nc-dispatch-biomonitor-critical = Biomonitor: critical condition.
nc-dispatch-biomonitor-dead = Biomonitor: no vital signs.

## Caller messages

nc-dispatch-call-sent = Call #{$id} sent.
nc-dispatch-call-sent-uncovered = Call #{$id} sent. You are outside the patrol area: do not count on a quick response.
nc-dispatch-call-unavailable = The service is unavailable.
nc-dispatch-call-no-signal = No signal here.
nc-dispatch-call-already = You already have an active call to this service.
nc-dispatch-call-cooldown = Wait before calling again.
nc-dispatch-call-no-account = You have no bank account.
nc-dispatch-call-no-money = Not enough money on your account.
nc-dispatch-accepted = Call #{$id} taken: {$responder} is on the way.
nc-dispatch-arrived = Call #{$id}: the crew is on scene.
nc-dispatch-closed = Call #{$id} is closed.
nc-dispatch-closed-expired = Call #{$id} is closed: nobody answered it in time.
nc-dispatch-closed-cancelled = Call #{$id} is called off.
nc-dispatch-refund = Call #{$id}: {$amount} €$ refunded.

## Dispatcher program

nc-dispatch-unavailable = This program is for NCPD and Trauma Team staff.
nc-dispatch-empty = No active incidents.
nc-dispatch-title = {$service}: incidents
nc-dispatch-status-open = waiting
nc-dispatch-status-accepted = taken by {$responder}
nc-dispatch-status-on-scene = on scene
nc-dispatch-row-title = #{$id} {$kind}
nc-dispatch-row-age = {$minutes} min
nc-dispatch-row-location = {$location}, floor {$z}
nc-dispatch-row-shots = Shots: {$shots}
nc-dispatch-row-caller = {$caller}: {$message}
nc-dispatch-row-plan = Plan: {$plan}
nc-dispatch-row-uncovered = Outside the patrol area
nc-dispatch-accept = Take
nc-dispatch-close = Close
nc-dispatch-not-found = The incident is gone.
nc-dispatch-already-taken = Someone already took this incident.

## Emergency program

nc-emergency-balance = Account balance: {$balance}
nc-emergency-message-placeholder = What happened?
nc-emergency-call = Call
nc-emergency-cancel = Call off
nc-emergency-price = Call price: {$price}
nc-emergency-free = Free call
nc-emergency-status-open = Call #{$id}: waiting for a crew.
nc-emergency-status-accepted = Call #{$id}: {$responder} is on the way.
nc-emergency-status-on-scene = Call #{$id}: the crew is on scene.
nc-emergency-cooldown = Next call in {$seconds} s.

## Trauma Team subscription

nc-trauma-subscription-title = Trauma Team subscription
nc-trauma-current-plan = Your plan: {$plan}, {$price} each payday.
nc-trauma-no-plan = No subscription. Trauma Team only answers subscribers for free; a one-off call is paid up front.
nc-trauma-unsubscribe = Cancel subscription
nc-trauma-plan-line = {$plan}: {$price}, arrival within {$minutes} min
nc-trauma-plan-active = Active
nc-trauma-subscribe = Subscribe
nc-trauma-subscribed = Trauma Team: {$plan} plan active.
nc-trauma-unsubscribed = Trauma Team subscription cancelled.
nc-trauma-already-subscribed = You already have this plan.
nc-trauma-billed = Trauma Team: {$plan} plan, -{$amount} €$.
nc-trauma-lapsed = Trauma Team: not enough money for the {$plan} plan. The subscription is cancelled.
nc-trauma-plan-silver = Silver
nc-trauma-plan-silver-desc = Biomonitor, crew and stabilization on site.
nc-trauma-plan-gold = Gold
nc-trauma-plan-gold-desc = Faster crew and evacuation to the clinic.
nc-trauma-plan-platinum = Platinum
nc-trauma-plan-platinum-desc = The fastest crew, evacuation even under fire.

## NCPD monitoring console

ent-NCComputerPoliceMonitor = NCPD city monitoring console
    .desc = The police network's view of the city. Every dot is a person; how fresh it is depends on the district.
ent-NCPoliceMonitorComputerCircuitboard = NCPD city monitoring console board
    .desc = A computer printed circuit board for an NCPD city monitoring console.
ent-NCEmergencyCartridge = emergency cartridge
    .desc = A program to call NCPD and Trauma Team.
ent-NCDispatchCartridge = dispatch cartridge
    .desc = NCPD and Trauma Team incidents.
nc-police-monitor-title = NCPD city monitoring
nc-police-monitor-hint = Right click a dot: name and job. No coverage in OCZ, the Hot Zone and the Reclamation Zone.
nc-police-monitor-count = Seen: {$count}
nc-police-monitor-no-job = no job
