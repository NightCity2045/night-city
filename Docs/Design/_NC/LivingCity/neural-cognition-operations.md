# Neural Cognition Service operations

The game remains authoritative and fully functional while the service is disabled or unavailable. The default
configuration is deterministic: `nc_neural.enabled` is false. The API key is loaded from the confidential server-only
`nc_neural.api_key` CVar. The server forwards it to the loopback cognition service in an Authorization header for one
request; it is never included in the gameplay payload, diagnostics, prototypes, Docker image, or repository. Plain
HTTP is accepted only for a loopback endpoint; a remote cognition endpoint must use HTTPS.

The service accepts structured facts and capability allowlists. It cannot call game APIs. The server rejects expired,
wrong-epoch, stale-version, malformed, over-budget, or non-allowlisted results and falls back deterministically.
An accepted result is still only a short-lived utility hint. BDI priorities and planner routing select the intention,
and the normal domain systems execute it. An in-person generated reply is recorded by the conversation authority and
spoken through the regular SS14 chat authority. Actor-to-actor turns do not recursively trigger neural replies.

Health endpoints are `/health/live` and `/health/ready`; Prometheus metrics are exposed at `/metrics`. Rotate the API
key outside the repository. During an incident, disable the policy first, then inspect the F7 neural counters and
container metrics. No catch-up request is issued after downtime.

## Local test with GPT-5.6 Luna

From `Tools/_NC/NeuralCognitionService`, start the service with:

```powershell
docker compose -f compose.yml -f compose.dev.yml up --build
```

The development override exposes only `127.0.0.1:8080`; the base compose file publishes no host port. Check
`http://127.0.0.1:8080/health/ready`. Set the credential only in the private server configuration, which must not be
committed:

```toml
[nc_neural]
enabled = true
endpoint = "http://127.0.0.1:8080/v1/cognition"
api_key = "sk-proj-..."
```

Then run the game server with that configuration. The repository preset intentionally contains an empty key:

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
