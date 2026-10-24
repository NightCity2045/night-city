# Native integration setup timeout, 2026-10-24

The dump places both active tests in client prototype/image initialization, before server startup and before their test bodies. It does not show an `NCMetaSnapshotSystem` selection wait or a managed lock cycle. A single dump cannot prove forward progress or determine the full reason for five minutes of startup time; CPU and disk contention from concurrent GUI instances is a plausible explanation reported by the coordinating task, rather than a conclusion established by this dump alone.

## Evidence

- Input: `testhost_30416_20261024T090553_hangdump.dmp`, 1,231,426,354 bytes.
- Analyzer: `artifacts/meta-dump-analyzer/MetaDumpAnalyzer.csproj`, using locally cached Microsoft.Diagnostics.Runtime 3.1.512801 and NETCore.Client 0.2.410101. No package download, content build, test execution, production change or engine change was performed.
- Runtime identified by ClrMD: `10.0.326.7603`; 52 managed thread entries, 19 alive.
- Full stacks and selected GC stack roots are in [managed-stacks.txt](managed-stacks.txt).
- VSTest's sequence records the conveyor-revision and clinical-service tests as incomplete. The cohort log records the **inactivity** timeout at five minutes; it is not evidence that either test body ran for five minutes.

| Thread | Observed state |
| --- | --- |
| Managed 16 / OS 29816 | NUnit `SetUpTearDownItem.RunSetUp` synchronously waits for an incomplete async setup task. |
| Managed 17 / OS 24476 | Same NUnit setup wait. |
| Managed 19 / OS 30504 | Cooperative execution in ImageSharp PNG decode / `TryFindFormatByDecoder`, called by `RsiLoading.LoadImages`, `RSIResource.LoadPreTexture`, sprite deserialization, `PrototypeManager.ResolveResults`, `ClientPrototypeManager.LoadDefaultPrototypes`, and `GameController.StartupContinue`. Current RSI root: `/Textures/Clothing/Head/Welding/flame_welding_mask.rsi`. |
| Managed 20 / OS 27416 | Closing a PNG stream in `SafeFileHandle.ReleaseHandle` / `BufferedFileStreamStrategy.Dispose`, with the same RSI/prototype/client-startup callers. Current RSI root: `/Textures/Structures/Machines/biofabricator.rsi`. |
| Other live managed threads | Test-runner/event-pump waits, timers, socket polling, idle pool workers, finalizer/runtime frames. No META persistence or snapshot wait appears in their stacks. |

`Robust.UnitTesting.Pool.TestPair.Init` awaits `GenerateClient()` before `GenerateServer()` (current source lines 82–83). Both active instance threads are still in client `Init`; this explains why no server startup stack or snapshot selection appears.

## Pool and CVar findings

- `Connected = false` still requests a full server/client pair. `GameTest.DoSetup` always awaits `PoolManager.GetServerClient`, and pair initialization only disconnects after generating both instances.
- The existing engine test harness already invokes `StartupContinue(DisplayMode.Headless)` and forces `res.texturepreloadingenabled=false`, startup GC collection off, and prototype file watching off (`RobustIntegrationTest.cs`, current lines 1107–1139).
- The captured work is **lazy** RSI loading from `SpriteComponent.AfterDeserialization`, not bulk `ResourceCache.PreloadTextures`. That hook loads and validates actual resource data even with bulk preload disabled. Repeating the existing preload CVar cannot remove this cost.
- `PoolSettings` has no client-CVar collection or server-only toggle. Normal pooled pairs preserve initialized client/resource caches; `Fresh`, `Destructive` and `NoLoadTestPrototypes` can force additional creation/destruction. `NoLoadContent` explicitly does not work with a client and would also invalidate this coverage.
- The current assembly permits two NUnit workers, consistent with both client initialization stacks being active simultaneously.

## Recommended next run

1. Stop the GUI server and graphical clients before running the isolated native cohort.
2. Run with `NUnit.NumberOfTestWorkers=1` and the ordinary reusable disconnected pool settings. Keep actual prototypes, RSI loading and semantic validation enabled.
3. Increase the external blame inactivity threshold to 600 seconds. The stock setup/pool path has no distinct startup wall-time budget; blame inactivity spans setup too. Existing test-specific bounded tick waits remain the appropriate body-level checks.
4. Record first-pair startup time and later pair-reuse time. If startup again reaches the threshold, compare a second dump's current RSI roots/stacks before concluding there is a deadlock or a permanently stuck decoder.

No fake resource provider, disabled prototype loading, skipped sprite deserialization or disabled RSI validation is recommended.
