# Night City — полная интеграция AstraGraph

## Исходное состояние

Уже сделано:

* AstraGraph добавлен как Git submodule;
* `Content.Shared` ссылается на `AstraGraph.Robust.Shared`;
* `Content.Server` ссылается на `AstraGraph.Robust.Server`;
* `Content.Client` ссылается на `AstraGraph.Robust.Client`.

Дальше задача — интегрировать AstraGraph в:

* startup сервера;
* startup клиента;
* permissions/admin;
* Resources;
* persistence;
* authoring;
* Studio;
* команды/меню;
* network;
* CI;
* release workflow.

---

# 1. Сначала проверить совместимость RobustToolbox

AstraGraph сейчас имеет:

```text
AstraGraph/Compatibility.json
```

В нём указан:

```text
compatibilityProfile = robust-api-v1
testedRobustCommit = ...
```

В Night City выполнить:

```bash
git -C RobustToolbox rev-parse HEAD
```

Сравнить с:

```text
AstraGraph/Compatibility.json
```

## Если SHA совпадает

Продолжаем.

## Если SHA другой

Не менять `Compatibility.json` просто чтобы убрать ошибку.

Нужно:

1. собрать Night City;
2. прогнать AstraGraph compatibility/integration tests против твоего Robust;
3. определить, совместим ли API;
4. только после этого добавить новый compatibility profile либо подтвердить текущий.

---

# 2. Проверить общую сборку

Из корня Night City:

```bash
dotnet build SpaceStation14.slnx
```

или актуальное имя solution.

При необходимости:

```bash
dotnet build SpaceStation14.slnx \
  -p:RobustToolboxRoot=./RobustToolbox
```

Отдельно AstraGraph билдить не нужно.

## Gate

До следующего этапа:

```text
Night City build = GREEN
```

---

# 3. Создать integration layer Night City

Не разбрасывать AstraGraph glue по всему Content.

Создать отдельные каталоги.

Например:

```text
Content.Shared/_NC/AstraGraph/
Content.Server/_NC/AstraGraph/
Content.Client/_NC/AstraGraph/
```

или другой ваш canonical namespace.

Рекомендуемая структура:

```text
Content.Shared/_NC/AstraGraph/
├─ AstraContentIntegration.cs
└─ AstraContentConstants.cs

Content.Server/_NC/AstraGraph/
├─ AstraServerIntegration.cs
├─ AstraAdminDirectory.cs
├─ AstraCommands.cs
└─ AstraServerStartup.cs

Content.Client/_NC/AstraGraph/
├─ AstraClientIntegration.cs
├─ AstraStudioCommand.cs
└─ AstraStudioMenuController.cs
```

Night City-specific код должен оставаться здесь.

---

# 4. Создать Resources/AstraGraph

В consumer repository:

```text
Resources/AstraGraph/
```

Рекомендуемая структура:

```text
Resources/AstraGraph/
├─ Systems/
├─ Behaviors/
├─ Libraries/
├─ Schemas/
└─ UI/
```

Это project-owned graphs.

Не хранить Night City graphs внутри:

```text
AstraGraph/
```

сабмодуля.

---

# 5. Runtime data

AstraGraph должен использовать:

```text
data/AstraGraph/
```

Runtime сам создаёт:

```text
Live/
History/
State/
Cache/
Backups/
Audit/
Overrides/
```

Этот каталог:

* не коммитить в Git;
* добавить в `.gitignore`, если `data/` ещё не исключён.

---

# 6. Проверить discovery EntitySystem'ов

Нужно удостовериться, что после добавления ProjectReference SS14 реально загружает:

```text
SharedAstraGraphSystem
ServerAstraGraphSystem
ClientAstraGraphSystem
```

Если текущий Robust автоматически discovers `EntitySystem` из referenced Content assemblies — дополнительная регистрация не нужна.

Если твой fork сканирует только Content assemblies, понадобится explicit load/register.

## Проверка

После запуска server/client должны появиться логи вроде:

```text
SharedAstraGraphSystem initialized successfully.
ServerAstraGraphSystem initialized...
ClientAstraGraphSystem initialized...
```

Не считать интеграцию успешной только по компиляции.

---

# 7. Server startup

На сервере должен существовать ровно один:

```text
ServerAstraGraphSystem
```

Он должен использовать тот же:

```text
AstraGraphHost
AstraGraphFacade
HotReloadManager
StorageLayout
```

на протяжении жизни server instance.

Не создавать второй AstraGraph runtime в Content.

---

# 8. Storage paths

Убедиться, что server использует именно consumer paths:

```text
<night-city>/Resources/AstraGraph
<night-city>/data/AstraGraph
```

а не:

```text
bin/.../Resources/AstraGraph
```

если ваша deployment структура отличается.

Это особенно важно для headless/release build.

---

# 9. Admin flag

В Night City `AdminFlags` добавить отдельный gate:

```text
AstraGraph
```

Выбрать реально свободный bit.

Например условно:

```csharp
AstraGraph = 1u << N
```

где `N` определяется текущим enum Night City.

Не копировать `1 << 24` вслепую.

---

# 10. Единственный host gate

Этот флаг означает только:

```text
может ли человек вообще входить в Astra authoring environment
```

Детальные права контролирует AstraGraph:

```text
View
EditDraft
Compile
Debug
Profile
PublishServer
PublishShared
Rollback
ManagePermissions
...
```

То есть:

```text
AdminFlags.AstraGraph
        ↓
вход разрешён
        ↓
Astra internal RBAC
```

---

# 11. Astra admin directory

Реализовать adapter:

```text
IAstraAdminDirectory
```

поверх Night City:

```text
IAdminManager
```

Пример ответственности:

```text
userId
↓
IAdminManager
↓
AdminData
↓
Flags
Rank
```

и возвратить AstraGraph:

```text
uint flags
string rank
bool sandbox
```

---

# 12. Не использовать reflection provider в production

Production path должен быть:

```text
ICommonSession
↓
IAstraAdminDirectory
↓
IAdminManager
↓
Astra permission resolver
```

а не чтение `AdminData`, `Flags`, `Rank` через reflection.

---

# 13. Важный prerequisite в AstraGraph

Текущий `ServerAstraGraphSystem` сам делает:

```csharp
new RobustAdminPermissionProvider()
```

Следовательно Night City должен иметь возможность передать:

```text
IAstraAdminDirectory
```

до создания AuthoringService.

AstraGraph должен предоставить один из API:

```csharp
ConfigureAdminDirectory(...)
```

или:

```csharp
ConfigurePermissions(IAstraPermissionProvider provider)
```

или constructor/composition injection.

## Финальный Night City flow

```text
IAdminManager
↓
NightCityAstraAdminDirectory
↓
RobustAdminPermissionProvider
↓
ServerAstraGraphSystem
```

Никакого hardcoded provider внутри production startup.

---

# 14. Permission profiles

Создать Night City mapping.

Минимум:

## Developer

```text
View
EditDraft
Compile
Debug
Profile
```

Без Publish.

## Publisher

дополнительно:

```text
PublishServer
Rollback
```

## Network Developer

```text
PublishShared
PublishPredicted
```

## Maintainer

```text
runtime state
permissions
engine-level access
```

---

# 15. Не привязывать publish к обычному admin

Наличие:

```text
AdminFlags.AstraGraph
```

не означает автоматически:

```text
PublishServer
```

Это отдельное Astra permission.

---

# 16. Live deadmin

Подписаться на изменение admin state.

При:

```text
deadmin
rank change
flag change
```

вызвать:

```text
PermissionProvider.InvalidateSession(session)
```

Дальше AstraAuthoringService должен:

```text
recompute user
push session.updated
disable forbidden Studio actions
```

Без reconnect.

---

# 17. Server authoring service

Использовать существующий:

```text
AstraAuthoringService
```

Не создавать Night City authoring protocol.

Он уже должен обслуживать:

```text
graph.list
graph.fetch
draft.save
draft.compile
draft.publish
history.list
history.rollback
catalog.query
debugger.command
profiler
```

---

# 18. Binding Catalog — индексировать Content

Это критический этап.

Astra Studio должна видеть не только Astra builtins, но и API твоего сервера.

На startup проиндексировать:

```text
Content.Shared
Content.Server
Robust.Shared
разрешённые Night City assemblies
```

через:

```text
AstraGraphFacade.Catalog
```

---

# 19. Не выставлять весь CLR API

Binding Catalog должен учитывать:

```text
SecurityProfile
Side
PredictionSafety
Purity
Cost
Access policy
```

Опасные API не должны автоматически попадать в Gameplay.

---

# 20. Night City custom bindings

Для domain-specific удобных операций создать Content-side bindings.

Например в будущем:

```text
NC.Entity.SpawnNpc
NC.Vehicle.GetEngine
NC.Cyberware.GetImplant
NC.Medicine.ApplyDrug
NC.Weapon.GetModule
```

Но они живут:

```text
Content.Shared/_NC/AstraGraph/Bindings
```

а не в AstraGraph Core.

---

# 21. Client Studio integration

На client используется:

```text
ClientAstraGraphSystem
```

Он умеет запускать Local Bridge и браузер.

Night City должен дать пользователю entry point.

Минимум console command:

```text
astra
```

или:

```text
astra.open
```

---

# 22. Команда Open Astra Studio

Команда должна:

1. проверить наличие client Astra system;
2. получить authoring session/token;
3. вызвать:

```text
ClientAstraGraphSystem.LaunchStudioAsync(...)
```

4. открыть browser.

---

# 23. Не открывать Studio напрямую без backend

Нельзя просто:

```text
client.LaunchStudioAsync(null)
```

Production Studio должен получать реальный:

```text
IAuthoringMessageHandler
```

или соответствующий client/server transport.

---

# 24. Server-issued launch token

Перед запуском Studio server должен:

```text
AstraAuthoringService.IssueLaunchToken(session)
```

Только если:

```text
CanEnterAstra(session) == true
```

---

# 25. Authoring transport между client/server

Нужно связать client-side Studio с server-side:

```text
AstraAuthoringService
```

Production path:

```text
Browser
↓
AstraLocalBridge
↓
SS14 Client
↓
Astra authoring network messages
↓
SS14 Server
↓
AstraAuthoringService
```

Если AstraGraph уже предоставляет transport adapter — использовать его.

Не проксировать browser напрямую на game server TCP endpoint.

---

# 26. Studio menu

После console command сделать нормальный UI entry.

Например admin/debug menu:

```text
Open Astra Studio
```

Доступна только при:

```text
AdminFlags.AstraGraph
```

---

# 27. Codespaces/headless отдельно

Когда AstraGraph DevHost готов, Night City server должен уметь attached mode:

```text
Content.Server
↓
AstraAuthoringService
↓
Astra Studio DevHost
↓
Codespaces forwarded port
```

Это dev-only.

Не смешивать с production client Local Bridge.

---

# 28. Первый .agraph

Для smoke test добавить:

```text
Resources/AstraGraph/Systems/DollToMothroach.agraph
```

Можно взять sample из:

```text
AstraGraph/samples/ContentIntegration/Resources/AstraGraph/
```

---

# 29. Но sample надо адаптировать к реальному Content API

Проверить:

```text
Content.Shared.Interaction.InteractHandEvent
```

и прочие referenced types именно в твоём build.

Не считать sample mechanic рабочей только потому, что JSON десериализуется.

---

# 30. Bootstrap test

Первый server startup должен:

```text
scan Resources/AstraGraph
↓
discover DollToMothroach
↓
compile
↓
register event subscription
↓
activate graph
```

В логах должна быть ясная информация:

```text
Graph discovered
Graph compiled
Revision active
```

---

# 31. Real event test

В игре или server test world:

```text
реальный Interact event
↓
Robust EventBus
↓
Astra Event Adapter
↓
graph
```

должен реально выполниться.

---

# 32. Hot reload test

С запущенным сервером:

1. открыть Studio;
2. изменить graph;
3. Compile;
4. Publish;
5. не делать `dotnet build`;
6. не перезапускать server.

Проверить, что mechanic изменилась сразу.

---

# 33. Persistence test

После Publish:

```text
data/AstraGraph/Live
```

должен содержать active source/revision.

Затем:

```text
stop server
start same binary
```

Graph должен восстановиться автоматически.

---

# 34. Rollback test

Publish:

```text
v1
v2
```

затем:

```text
Rollback
```

Проверить:

```text
code
schema
state
subscriptions
```

а не только bytecode revision.

---

# 35. Networking

Убедиться, что:

```text
RobustNetManagerTransport
```

работает в consumer build.

Проверить:

```text
server handshake
client missing graph detection
package transfer
ready
```

---

# 36. Shared graph test

Создать простой Shared graph.

Проверить:

```text
server revision
=
client revision
```

и один:

```text
ActivationTick
```

---

# 37. Prediction test

После Shared убедиться, что:

```text
SharedPredicted
```

использует:

```text
IGameTiming
prediction history
rollback
replay
```

Не начинать с боевой механики.

Сначала простейший counter/state graph.

---

# 38. Astra UI test

Создать тестовый UI graph:

```text
Label
Button
LineEdit
```

Проверить native Robust controls.

---

# 39. BUI test

Следующий UI:

```text
server state
↓
AstraBuiState
↓
client window
```

Button:

```text
client action
↓
server validation
↓
state update
```

---

# 40. Debugger test

Поставить breakpoint из Studio.

Вызвать mechanic.

Проверить:

```text
BreakpointHit
graph
node
locals
event
```

---

# 41. Profiler test

Проверить, что server отдает:

```text
invocations
time
native calls
query work
```

и Studio получает поток.

---

# 42. Добавить Content integration tests

Не тестировать всё вручную.

Создать в Night City test project набор:

```text
AstraGraphIntegrationTests
```

---

# 43. Test — boot

Поднять Robust server simulation.

Проверить загрузку:

```text
ServerAstraGraphSystem
```

---

# 44. Test — EventBus

Настоящий Robust event:

```text
RaiseLocalEvent
↓
Astra graph
```

---

# 45. Test — mixed ECS

```text
native component
+
Astra dynamic component
```

одновременно.

---

# 46. Test — permissions

Проверить:

```text
no flag → denied
Developer → compile yes / publish no
Publisher → publish yes
deadmin → access removed
```

---

# 47. Test — restart

```text
runtime A
publish
persist
destroy

runtime B
startup
restore
```

---

# 48. Test — Studio authoring

Минимальный integration test:

```text
auth
graph.list
draft.save
compile
publish
```

---

# 49. CI Night City

CI должен обязательно:

```text
git submodule update --init --recursive
```

до restore/build.

---

# 50. CI должен фиксировать AstraGraph commit

Не:

```text
git -C AstraGraph pull
```

во время CI.

Consumer commit уже содержит точный submodule SHA.

---

# 51. CI должен проверять engine compatibility

Pipeline:

```text
Night City commit
↓
AstraGraph submodule SHA
↓
RobustToolbox submodule SHA
↓
Compatibility check
↓
build
↓
tests
```

---

# 52. Version matrix в будущем

Когда Night City обновит Robust:

```text
274 → 282
```

не менять AstraGraph Core.

Проверить новый engine profile согласно:

```text
09 — Engine Version Compatibility
```

---

# 53. Git workflow

Обновлять AstraGraph в Night City так:

```bash
cd AstraGraph
git fetch
git checkout <verified-commit>
cd ..

git add AstraGraph
git commit -m "Update AstraGraph"
```

Не тащить latest автоматически.

---

# 54. Production safety

В production:

```text
Studio DevHost = OFF
```

Доступ через:

```text
authenticated SS14 client
+
AdminFlags.AstraGraph
+
Astra RBAC
```

---

# 55. Audit

Publish/rollback должны писать:

```text
user
graph
old revision
new revision
time
operation
result
```

в Astra audit.

---

# 56. Логи

Night City startup должен явно показывать:

```text
AstraGraph version
Compatibility profile
Robust compatibility
Graphs discovered
Graphs activated
Authoring enabled/disabled
```

---

# 57. Fail-fast

Если:

```text
Compatibility.json missing
Robust incompatible
required Astra system failed
```

не продолжать молча.

Server startup должен дать понятную ошибку.

---

# 58. Не делать Night City fork AstraGraph

Не изменять сабмодуль под NC-specific API.

Если AstraGraph не хватает generic integration hook — добавить его upstream в AstraGraph и обновить submodule SHA.

Night City-specific implementation остаётся в Content.

---

# 59. Рекомендуемый порядок реализации

## Stage A — Bootstrap

1. Build общей solution.
2. `Resources/AstraGraph`.
3. Проверить загрузку трех EntitySystem.
4. Проверить StorageLayout.
5. Проверить compatibility.

## Stage B — Server

6. `AdminFlags.AstraGraph`.
7. `IAstraAdminDirectory`.
8. Permission provider injection.
9. Binding Catalog Content assemblies.
10. Bootstrap graph.

## Stage C — Studio

11. Server launch token.
12. Client authoring transport.
13. Console command `astra.open`.
14. Local Bridge.
15. Web Studio.

## Stage D — Runtime

16. Real event.
17. Hot reload.
18. Rollback.
19. Restart persistence.
20. Debugger/profiler.

## Stage E — Network/UI

21. Client package sync.
22. Shared activation.
23. Prediction.
24. Astra UI.
25. BUI.

## Stage F — Production

26. Audit.
27. Tests.
28. CI.
29. Permissions hardening.
30. Release acceptance.

---

# 60. Definition of Done для Night City integration

Интеграция считается полной, когда:

1. Night City собирается одной общей командой вместе с AstraGraph.
2. ServerAstraGraphSystem запускается автоматически.
3. ClientAstraGraphSystem запускается автоматически.
4. SharedAstraGraphSystem использует реальный Robust ECS.
5. `Resources/AstraGraph` обнаруживается автоматически.
6. Graph компилируется без `dotnet build`.
7. Publish применяется без restart.
8. Server restart восстанавливает graph.
9. Rollback работает.
10. Admin flag контролирует доступ.
11. Developer не может Publish.
12. Publisher может Publish.
13. Deadmin работает live.
14. Studio запускается из Night City.
15. Studio получает реальный Binding Catalog Night City.
16. Реальный Robust event запускает graph.
17. Mixed ECS query работает.
18. Native bindings Night City работают.
19. Client получает active graph revisions.
20. Shared graph синхронно активируется.
21. Prediction работает.
22. Astra UI создаёт native controls.
23. BUI проходит server-authoritative validation.
24. Debugger работает.
25. Profiler работает.
26. CI поднимает submodules и проходит tests.
27. Production DevHost выключен.
28. NC-specific API отсутствует в AstraGraph Core.
29. AstraGraph и Robust commits зафиксированы consumer commit'ом.
30. Первый реальный Night City mechanic работает полностью через `.agraph`.

# Конечная архитектура

```text
Night City Content
        │
        ├───────────────┐
        │               │
        ▼               ▼
Content bindings   Admin integration
        │               │
        └───────┬───────┘
                ▼
       AstraGraph.Robust
                │
                ▼
         AstraGraphFacade
                │
     ┌──────────┼───────────┐
     ▼          ▼           ▼
 Runtime    Authoring    Persistence
     │          │           │
     ▼          ▼           ▼
 Robust ECS  Studio Web   data/AstraGraph
```

При этом:

```text
Resources/AstraGraph
```

— исходники игровых mechanics,

а:

```text
AstraGraph/
```

— независимый reusable submodule.
