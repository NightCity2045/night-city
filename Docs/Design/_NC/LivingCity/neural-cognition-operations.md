# Neural Cognition Service operations

Status: Current for Physical GPT A-Life v3.

The game remains authoritative while the service is disabled or unavailable. Physical NPCs stay in the world, but
they do not receive a substitute deterministic social or life-planning brain. They may finish an already accepted
safe command or use a bounded safety reflex, then wait for the provider. The game server reads the confidential
`nc_neural.api_key` CVar and forwards it only as the bearer of a bounded cognition request. The credential is never
included in gameplay payloads, diagnostics, prototypes, or logs. Plain HTTP is accepted only for a loopback endpoint;
a remote cognition endpoint must use HTTPS.

The service accepts structured facts and capability allowlists. It cannot call game APIs. The server rejects expired,
wrong-epoch, stale-version, or malformed envelopes; each proposed physical command is authorized independently before
normal gameplay systems execute it. Local token, session, weekly, request-kind, and per-ActorId spending quotas do not
disable GPT. Provider availability and account balance are the spending authority. Usage persistence is telemetry only.

Health endpoints are `/health/live` and `/health/ready`; Prometheus metrics are exposed at `/metrics`. Rotate the API
key outside the repository. During an incident, disable the policy first, then inspect the F7 neural counters and
container metrics. No catch-up request is issued after downtime.

## Local test with GPT-5.6 Luna

From `Tools/_NC/NeuralCognitionService`, start the service without Docker:

```powershell
.\run-local.ps1
```

Alternatively, start it with Docker:

```powershell
docker compose -f compose.yml -f compose.dev.yml up --build
```

The development override exposes only `127.0.0.1:8080`; the base compose file publishes no host port. Check
`http://127.0.0.1:8080/health/ready`. The private server configuration owns the provider credential:

```toml
[nc_neural]
enabled = true
endpoint = "http://127.0.0.1:8080/v1/cognition"
api_key = "replace-with-the-provider-key"
```

Then run the game server with that configuration. The repository Luna preset intentionally leaves the key empty:

```powershell
dotnet run --project Content.Server -- --config-file Resources/ConfigPresets/_NC/neural-luna.toml
```

Use the F7 Living City page to monitor provider configuration, pending/coalesced requests, accepted results, usage
telemetry persistence, timeouts, validation rejects, and temporary circuit state.

## Test without a provider

Run the server normally without a provider to verify physical persistence, bootstrap, sensorium, queue telemetry and
safe failure behavior. This does not validate autonomous decisions: GPT is the primary ordinary decision source and
no local BDI/Utility planner replaces it.

For the isolated station exercise, select game preset `NCReachNpcTest` (`reach-npc-test` / `reachgpt`). It loads Reach
and creates exactly 30 persistent physical NPCs at authored job spawn points. The same bodies, neural coordinator,
sensorium and action authorities are used on maps with or without CityGraph; station destinations are inferred from
job spawn points, departments and navigation beacons.
