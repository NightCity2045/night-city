# Neural Cognition Service operations

Status: Current for Physical GPT A-Life v3.

Transport recovery (2026-09-05): each actor retains separate pending dialogue and autonomous requests, with one
HTTP request at a time per body. There is no cross-actor concurrency cap or reserved-slot pool. Background polling does not
supersede an outstanding response. The timeout is 45 wall-clock seconds; up to two delayed transport retries preserve
the original dialogue correlation. Failures never open a global dispatch circuit. Retry delays and conversation
expiry use active-world time (unscaled elapsed simulation time). The five-minute idle window covers three
45-second attempts plus the 30/60-second retry delays, excluding any additional queue backlog.

These fixes do not certify autonomous departmental shifts. Existing station adapters expose individual operations;
complete supply, preparation, delivery and patient-care workflows still require implementation and live verification.

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

The Reach NPC test presets and their antagonist scenario have been removed. The Reach map itself remains.
GPT is now opt-in through an existing NarrativeActorProfile; ordinary residents cannot submit provider requests.
Local ordinary-NPC life/work behavior remains incomplete; disabling GPT is not a completed local AI implementation.
