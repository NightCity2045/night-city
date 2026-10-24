# Owned native testhost snapshot, 24 October 2026

Only PID25204 was attached after its exact creation time was verified: `2026-10-24T12:14:23.7688024Z` (15:14:23.7688024 local). DiagnosticsClient captured a TRIAGE dump at12:26:11Z,7,634,545bytes. Identity, file and DAC hashes are in `identity.json`. This diagnostic did not stop the testhost or start a profiler. The original run subsequently ended; no further process was attached.

Cached ClrMD3.1.512801 decoded33 managed threads with the exact installed .NET10.0.3 DAC (`10.0.326.7603`, `C:/Program Files/dotnet/shared/Microsoft.NETCore.App/10.0.3/mscordaccore.dll`). Default DAC lookup initially attempted the unavailable symbol host; no symbol download succeeded. Explicit local DAC analysis produced `managed-stacks-local-dac.txt`.

The active cooperative server thread is managed36 / OS20816:

```
SpanHelpers.Memmove / StringBuilder.Append
RelationalIndexExtensions.GetDefaultDatabaseName
RelationalModel.PopulateTableConfiguration / Create
SqliteMigrationsSqlGenerator.RewriteOperations / Generate
Migrator.GenerateUpSql / Migrate
ServerDbSqlite..ctor / ServerDbManager.Init
Content.Server.Entry.EntryPoint.Init
BaseServer.Start / ServerIntegrationInstance.Init
```

NUnit managed16 is waiting for async setup. Client managed18 is waiting in `RobustIntegrationTest.IntegrationGameLoop.Run`. Resolved stacks do not show the earlier RSI image-loading location or a META VM/snapshot wait. One snapshot identifies the sampled startup stage; it does not prove EF migration deadlock, identify a slow table/index, or establish the cause of elapsed startup time.

The completed run reports109 cases:5 passed,104 failed, duration10m57s. Its first substantive setup error is a duplicate subscription for `NCMetaTerminalComponent` / `GetVerbsEvent<...>` in `NCMetaCommissioningSystem.Initialize` line38. Later `Pair == null`/teardown failures are cascading fixture failures. The EF startup sample and the final duplicate-subscription exception are distinct evidence; neither is described as the previous RSI hang. Parent owns the commissioning fix and the next coherent run.
