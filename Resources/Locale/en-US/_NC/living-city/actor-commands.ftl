cmd-ncactorlist-description = List persistent Narrative Actors with full neural profiles.
cmd-ncactorlist-help = Usage: {$command} [key or name filter]
cmd-ncactorspawn-description = Ensure an existing persistent GPT Actor is physical near an online player.
cmd-ncactorspawn-help = Usage: {$command} <actor key> [online player]
cmd-ncactor-hint-key = Narrative Actor key
cmd-ncactor-hint-player = Online player; omitted means yourself

cmd-ncactorlist-count = Narrative Actors found: {$count}.
cmd-ncactorlist-entry = {$key} | {$name} | profile {$profile} | Actor {$actor} | {$status}
cmd-ncactor-status-not-imported = not imported
cmd-ncactor-status-profile-mismatch = persistent profile mismatch
cmd-ncactor-status-physical = physical entity {$entity}
cmd-ncactor-status-abstract = not physical; recovery pending
cmd-ncactor-status-unknown = unknown

cmd-ncactor-error-unknown = Unknown Narrative Actor key or name: {$key}.
cmd-ncactor-error-ambiguous = Narrative Actor key or name is ambiguous: {$key}. Use its profile-qualified key from ncactorlist.
cmd-ncactor-error-player-offline = Player {$player} is not online.
cmd-ncactor-error-server-needs-player = The server console must specify an online player.
cmd-ncactor-error-player-no-entity = Player {$player} is not controlling a physical entity.
cmd-ncactor-error-no-city-coordinates = Player {$player} is not inside a loaded Night City Z-network.
cmd-ncactor-error-not-imported = Actor {$key} has not been imported. Import the configured population profile first.
cmd-ncactor-error-profile-mismatch = Actor {$key} exists, but its persistent Narrative Actor profile is missing or outdated. Re-run the population import migration.
cmd-ncactor-error-unknown-reason = no diagnostic was supplied
cmd-ncactor-error-materialization = Could not ensure a physical entity for Actor {$key}: {$reason}

cmd-ncactorspawn-requested = Actor {$key} ({$name}, {$actor}) physical lifecycle request {$request}: {$status}.
cmd-ncactorspawn-completed = Actor {$key} request {$request}: {$status} as entity {$entity}.
cmd-ncactorspawn-failed = Actor {$key} request {$request} {$status}: {$reason}
cmd-ncactorspawn-result-unavailable = Actor {$key} request {$request} no longer has an observable result.

cmd-ncactor-neural-ready = Neural Cognition provider is available; current budget mode: {$mode}. The Actor's personality, memory, perception, autonomy, and GPT allowlist are active.
cmd-ncactor-neural-fallback = The Actor remains physical, but GPT cannot currently spend tokens; provider retry and local safety reflexes remain active. Budget mode: {$mode}.
cmd-ncactor-neural-mode-normal = normal
cmd-ncactor-neural-mode-economy = economy
cmd-ncactor-neural-mode-restricted = restricted
cmd-ncactor-neural-mode-fallback = fallback only

cmd-ncactor-spawn-status-queued = queued
cmd-ncactor-spawn-status-loading = loading persistent state
cmd-ncactor-spawn-status-retrying = waiting for a safe floor
cmd-ncactor-spawn-status-spawned = physical
cmd-ncactor-spawn-status-already = already physical
cmd-ncactor-spawn-status-rejected = rejected
