<!-- SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0 -->

# Garage application

This package is ordinary META source, MUI documents and styles. Its business operations use the generic `meta.services.ServiceOrders` adapter. It has no garage-specific C# backend, physical repair shortcut or authority to transfer money.

Deploy the `service-orders` slot to an owned native service desk. The staff diagnostics slots require explicit read grants to the selected physical ECU and vehicle instruments; a source slot name does not grant access to a customer's car. The native workshop/service UI, real tools and physical parts perform repairs. The staff action records readiness after that work; it never writes vehicle condition.

Publish only the `customer` view. Its public actions are `refresh`, `create`, `pay`, `complete` and `cancel`. Keep `staff` private to the provider and explicitly admitted staff. Invoice/assignment/work actions remain subject to native provider/executor policy even when a client forges an action name.

Views are synchronous and read only the native session-scoped `cachedList`. `refresh` calls the asynchronous filtered `listOrders`, then the ordinary action completion rebuilds the view. Each read verifies the opaque live UserRef/SessionRef proof. No customer order rows are stored in guest globals. The `orderChanged` watch contains only an order reference and revision; native cache invalidation and view refresh do not broadcast the subject, customer or invoice.

Mutation request labels are source constants such as `create-order` and `quote-order`. The native adapter binds them to the current server-created application ActionRequest ID. No player enters a technical idempotency or session identifier. `take` uses the native `self` executor alias, resolved from the authenticated actor.

The visible business reference is `ORD-` followed by the first eight hexadecimal characters of the server-issued order identity. Selecting a table row passes its hidden stable identity to the declared `select` action. Native lookup also accepts business references only within the current provider desk and rejects collisions with `Conflict`; it never treats a list position as an identity. Full identifiers stay inside the native/META action path and hidden form data.

The selected order lives in a bounded list keyed by a live `SessionRef`, compared only through `app.sameSession`. A customer's selection never becomes another session's selection. Closed references are pruned on the next selection action, and a selection is projected only while the native private cache still includes that permitted order. A client that forges a hidden key still faces current native visibility and mutation authorization.

Issuing an invoice does not debit an account. The customer's `pay` action requests a native confirmation tied to the exact invoice ID, hash, amount, payer and provider. Payment becomes visible only after the native ledger confirms it. After a successful order mutation, a failed list refresh still returns success for the completed mutation, avoiding a misleading invitation to repeat it.

The initial stale cache shows the refresh action. A rejected or expired private cache displays no previous customer's rows. The shared VM retains only a non-sensitive change revision between sessions. English and Russian labels are selected from the trusted AppContext locale.
