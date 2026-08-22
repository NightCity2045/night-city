# Neural Cognition Service operations

The game remains authoritative and fully functional while the service is disabled or unavailable. The default
configuration is deterministic: `nc_neural.enabled` is false. The API key exists only in the isolated service and is
never passed to the game server, a CVar, a prototype, or the repository.

The service accepts structured facts and capability allowlists. It cannot call game APIs. The server rejects expired,
wrong-epoch, stale-version, malformed, over-budget, or non-allowlisted results and falls back deterministically.
An accepted result is still only a short-lived utility hint. BDI priorities and planner routing select the intention,
and the normal domain systems execute it. An in-person generated reply is recorded by the conversation authority and
spoken through the regular SS14 chat authority. Actor-to-actor turns do not recursively trigger neural replies.

Health endpoints are `/health/live` and `/health/ready`; Prometheus metrics are exposed at `/metrics`. Rotate the API
key outside the repository. During an incident, disable the policy first, then inspect the F7 neural counters and
container metrics. No catch-up request is issued after downtime.

## Local test with GPT-5.6 Luna

From `Tools/_NC/NeuralCognitionService`, set `OPENAI_API_KEY` in the shell environment, then start the service with:

```powershell
docker compose -f compose.yml -f compose.dev.yml up --build
```

The development override exposes only `127.0.0.1:8080`; the base compose file publishes no host port. Check
`http://127.0.0.1:8080/health/ready`, then run the game server with the explicit opt-in preset:

```powershell
dotnet run --project Content.Server -- --config-file Resources/ConfigPresets/_NC/neural-luna.toml
```

Use the F7 Living City page to monitor the neural budget mode, queue, accepted results, timeouts, validation rejects,
and circuit state. Do not use the Luna preset for an ordinary deterministic test.

## Deterministic test without a provider

Run the server normally, without the neural preset and without the service. Utility, StateTree, Reactive, HTN,
Partial-Order, MCTS and Conversation-StateTree planning continue to operate; neural-eligible actors simply receive no
external suggestion. The district bootstrap population can be imported idempotently from F7 with profile
`NCLivingCityDistrictBootstrapPopulation`.

The current authored geography contains only district zones. Imported residents therefore participate in abstract
A-Life immediately, but physical materialization intentionally waits for mapper-authored street, building or apartment
zones with valid pedestrian spawn candidates.
