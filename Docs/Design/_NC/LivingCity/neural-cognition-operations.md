# Neural Cognition Service operations

The game remains authoritative and fully functional while the service is disabled or unavailable. Enable the
`ncNeuralPolicy` only after the private Docker network and `OPENAI_API_KEY` secret are configured.

The service accepts structured facts and capability allowlists. It cannot call game APIs. The server rejects expired,
wrong-epoch, stale-version, malformed, over-budget, or non-allowlisted results and falls back deterministically.

Health endpoints are `/health/live` and `/health/ready`; Prometheus metrics are exposed at `/metrics`. Rotate the API
key outside the repository. During an incident, disable the policy first, then inspect the F7 neural counters and
container metrics. No catch-up request is issued after downtime.
