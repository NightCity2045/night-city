<!-- SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0 -->

# Isolated META workload capture

These tools execute the actual server workload driver. They do not build binaries or substitute a
test VM/native API. Use only the coordinated immutable diagnostic bundle after its native functional
gate. `run.py` owns a diagnostic CLI session and a fresh isolated SQLite environment by default;
`--target server` uses an explicitly configured `SS14_MCP_SERVER` target instead. Credentials stay in
the existing bridge environment variables and are not written to the run manifest.

```powershell
python Tools/_NC/Meta/Performance/run.py --cli <built-diagnostic-cli.dll> --build-id <verified-cohort-uuid> --workload A --mode Normal --warmup 30 --seconds 60 --output artifacts/meta-performance/A-normal-01
python Tools/_NC/Meta/Performance/reduce.py artifacts/meta-performance/A-normal-01
python Tools/_NC/Meta/Performance/matrix.py --cli <built-diagnostic-cli.dll> --build-id <verified-cohort-uuid> --repeats 3 --seconds 30 --output artifacts/meta-performance/balanced-matrix-01
```

Repeat A, B, C and C-resident with balanced `--baseline`/candidate ordering, at least three repeats,
fixed seeds and identical source/resource/DB/world configuration. Normal targets2events/s per active
service; every tenth input requests a real reply. Gas/handler is recorded rather than assumed to
equal500. Modes Burst, Heavy and Hostile change real native input timing/payload; Hostile triggers
a bounded subset of the same verified programs into an infinite handler and leaves fair peers.
The driver admits through normal persistence, ownership, service policy, hardware and runtime APIs.
It fails if the requested count is refused, including C-resident memory/admission limits.
Provisioning up to2000 installations and640 distinct source/project artifacts can take many minutes;
this is outside the warmup/capture and is not a throughput result. The worker remains out of process.

Forty physical nonliving test robots move through native steering. They are not fake player
connections, a replacement Living City brain, or forty idle sockets. Baseline retains the same
physical devices/robots/external native message inputs and starts no guest programs. The fixture
uses authored native APC receivers with `needsPower:false`; RAM/gas/process quotas are unchanged.
It does not prove a naturally powered grid or wide-area network under this arrangement.
The isolated database retains the test records. Teardown archives its fixture actors and deletes
only its map; use a fresh environment/snapshot between comparative runs. Do not run this against
a production database or a populated playtest world.

`manifest.json` and `samples.jsonl` are sealed with SHA256. Source/resource file hashes are
client-declared provenance, identified as such in native status. The server independently hashes
its actually loaded server assembly, generated workload sources, trusted catalog and verified
artifact set. The manifest distinguishes those observations. An overflow retains aggregate counts
but invalidates exact raw quantiles. The reducer never sums p99 values or calls elapsed time CPU.

The current source provides measurement paths; no load acceptance result is implied before the
coordinated native runs. `EngineTickElapsed` pairs the existing `Robust.GameLoop` TickStart/TickStop
provider on the actual game thread. The listener records elapsed time for the complete Tick callback
and excludes sleep. Missing pairs and partial capture edges are explicit. Raw tick quantiles are
available only when provider/pair/sample coverage is complete. `compare.py` compares a sealed
baseline and candidate with identical loaded assembly/source semantics, inventories, seed and
capture configuration. A difference between independent-run p99 values is labelled as such; it is
not a p99 of paired differences or final acceptance. Use at least three balanced repetitions.

```powershell
python Tools/_NC/Meta/Performance/compare.py artifacts/meta-performance/A-baseline-01 artifacts/meta-performance/A-normal-01 --output artifacts/meta-performance/A-pair-01.json
```

`fleet.py` launches an explicitly verified private acceptance graph with its pre-build resource
snapshot, one isolated loopback server and up to fifty **actual native headless clients**. Every
client has a unique self-contained user-data directory. Startup concurrency defaults to two and is
bounded at four. Game processes have declared per-client/aggregate RSS ceilings and a physical
memory reserve; crossing a bound aborts only handles created by this fleet. Separate lightweight
CLI controllers and one-second process sampling consume resources too; free-memory monitoring
covers their pressure, while the exported game RSS table identifies its narrower scope.

```powershell
python Tools/_NC/Meta/Performance/fleet.py --workspace D:/projects/night-city --cli <built-diagnostic-cli.dll> --build-id <verified-cohort-uuid> --server artifacts/meta-acceptance/<uuid>/bin/Content.Server/Content.Server.dll --client artifacts/meta-acceptance/<uuid>/bin/Content.Client/Content.Client.dll --resources artifacts/meta-acceptance/<uuid>/Resources --engine-resources RobustToolbox/Resources --clients 13 --sessions 50 --views-per-client 4 --startup-parallel 2 --seconds 28 --action-hz 1 --output artifacts/meta-performance/app-50-01
python Tools/_NC/Meta/Performance/reduce_frontend.py artifacts/meta-performance/app-50-01
```

The server admits a fixed source app through the actual worker/export verifier/release/install/
service-consent/runtime/MUI path. A fixture-only route attaches real connected users through the
native mind/ticker systems to fixed MobHuman bodies on one physical grid. It does not construct
ICommonSession, ActorComponent or InGame status. At most50 bodies/64 owned fixture entities are
allowed; production app64/client4 quotas remain unchanged. A native renderer bridge accepts only
declared typed actions and uses ordinary BUI transport. Each frontend must observe its own initial
snapshot, reducer revision, ACK and successful completed action. The fleet exports bounded raw
client monotonic action→result observations and state JSON-body byte counts, not guessed socket
bytes. It fails on missed requested drive rate, missing results/ACKs, overflow or real admission
failure. Headless transport/reducer coverage does not claim graphics or RSI rendering performance.
The generic MCP environment launcher remains limited to three clients; this dedicated supervisor
owns the larger fleet without changing that limit.

The default layout is twelve actual users with four distinct physical BUI owner windows each,
plus one actual user with two: **13 connected users, 50 native renderer sessions**, one backend
VM. Four physical UI terminals are shared by the users; every route still checks live actor,
power and unobstructed interaction range. Native admission v2 explicitly returns those owners;
an older cohort is rejected rather than repeatedly opening the same window. Opening, capture,
declared typed actions and results all use the ordinary native paths.

`frontend_driver.py` schedules at most one outstanding action/session and one submission/client
per poll, with round-robin fairness and phase offsets. Late slots are skipped and reported as
incomplete, without catch-up bursts. At28seconds×1Hz, each four-view controller uses at most
120 mutations (112 actions plus4 capture and4 stop), within the existing128-entry/10minute
retention. No controller is recycled. A60second×0.5Hz run fits exactly128;60seconds×1Hz with
four views is rejected before startup. Actions stop at the common deadline; completion drain
and collection wall time are recorded separately. The reducer requires all raw samples and
ACKs, counts users separately from views, and refuses merged latency quantiles on incomplete
coverage. These helper tests and source changes are not a measured50-session result.

`TraceCollector` uses the cached Microsoft Diagnostics libraries and the EventPipe endpoint of
**one proven owned server PID**. It never starts system-wide WPR/ETW. Its request must reference the
sealed `processes.json` produced by the fleet, and PID creation time is checked around capture.
Its64MiB runtime buffer, trace byte ceiling, duration, event/sample/stack bounds and own-session stop
are explicit. Build it outside game output during a coordinated tools slot; it has no game project
reference. No tool installation occurs during a run.

```powershell
python Tools/_NC/Meta/Performance/cpu_capture.py --collector artifacts/meta-trace-collector/bin/TraceCollector/release/TraceCollector.dll --processes artifacts/meta-performance/app-50-01/processes.json --seconds 60 --output artifacts/meta-performance/app-50-01/cpu-01
```

Run the collector only during the intended stable measurement interval, after all preparation and
warmup. Retain its exact capture timestamps to reject/trim misaligned intervals. The parser
identifies the actual game-loop thread from Robust.GameLoop events, retains stack sample types,
method groups, unresolved/lost-event counts, observed sample intervals and paired CLR suspension
elapsed times. OS thread CPU endpoints are separate observations. Stack sample counts are not
exact per-system CPU durations or sub-millisecond CPU p99, and no multiplier turns wall time into
CPU. Review native/managed/blocked sample coverage, rundown resolution, uncertainty and thread
lifetimes before attributing costs. Collector overhead requires enabled/disabled baseline runs.
Host CPU readings never feed gameplay battery accounting.

Still required are the actual A/B/C/C-resident and50-session measurements, complete attribution of
META-originated native system/event work, mixed native domain jobs, compiler flood, slow real DB,
and the separate simultaneous2000-record boot wave. Provisioning2000 records before recording
is not a cold-boot-wave result. A failed requested scale is a recorded failure, not permission to
replace users with projections or silently reduce process count.
