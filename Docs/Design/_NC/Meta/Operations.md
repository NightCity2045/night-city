# META: эксплуатация и проверяемая сборка

Полный scope и критерии приёмки заданы в `ImplementationPlan.md`. Выполненные проверки и незакрытые gates перечислены в `implementation-status.md`; наличие кода не означает завершённой приёмки.

## Текущие границы поддержки

Compiler worker использует Windows Job Objects для обязательных ограничений дочернего процесса. Текущий проверяемый bundle предназначен для Windows x64/.NET 10. Остальные платформы пока не заявлены поддерживаемыми: отсутствие обязательной изоляции должно приводить к отказу запуска worker. Успешная компиляция на другой ОС сама по себе не подтверждает sandbox.

Миграции и META DB tests проверены на SQLite и PostgreSQL 18.6. Backup comparator проверяет данные, схему и индексы, а для PostgreSQL также состояние последовательностей. Эти результаты не заменяют восстановление игрового мира и аварийный restart процесса. Принятый масштаб A/B/C/C-resident и 50 frontend views пока отсутствует; опубликованные лимиты остаются admission policy до фактических измерений.

Диагностические editor input проверки вызывают настоящие content handlers для текста, composition и key bindings. Они не подтверждают Windows IME candidate window или физическую клавиатуру; это отдельная графическая проверка. Clipboard проверяется через настоящий clipboard manager с короткой восстанавливаемой lease и явным освобождением до остановки клиента.

Изолированный pristine SQLite prefill включается только четырьмя явными pins: `SS14_MCP_SQLITE_SEED_ID`, `SS14_MCP_SQLITE_SEED_SHA256`, `SS14_MCP_SQLITE_MANIFEST_SHA256`, `SS14_MCP_SQLITE_HELPER_SHA256`. MCP проверяет неизменяемый package и helper bundle, создаёт только новую owned БД до запуска сервера и оставляет обычный `Migrate()` включённым. Без всех четырёх pins частичная конфигурация отклоняется. Этот механизм ускоряет acceptance startup и не предназначен для замены существующей игровой БД.

## Остановка и отзыв

Команда `ncmeta` требует `AdminFlags.Admin` либо локальную серверную консоль.

```text
ncmeta status [hostNetEntity]
ncmeta repro [hostNetEntity]
ncmeta stop process <generation-guid>
ncmeta stop device <hostNetEntity>
ncmeta stop principal <canonical-party-id>
ncmeta enabled false
ncmeta enabled true
ncmeta block <artifact-or-blob-sha256>
ncmeta unblock <artifact-or-blob-sha256>
ncmeta quarantine <artifact-or-blob-sha256>
ncmeta revoke <installation-guid> <reason>
```

Глобальная остановка завершает поколения, отзывает их runtime grants и запрещает новые запуски. Доменные системы сохраняют ручные пути управления. Включение разрешает новую обычную активацию; остановленный стек VM не восстанавливается. Блокировка принимает semantic artifact hash или SHA неизменяемых bytes. Некорректный список блокировки останавливает META до исправления политики.

`nc_meta.enabled` и `nc_meta.revoked_artifacts` — серверные архивируемые CVars. Команда запрашивает сохранение серверной конфигурации; API движка не возвращает подтверждение записи. Перед перезапуском оператор проверяет журнал и сохранённый TOML. Не считать ответ «save requested» durable ACK. Для аварийной загрузки можно заранее записать `enabled = false` в секции `[nc_meta]` конфигурации сервера.

`revoke` использует отдельный native administrative путь: свежая проверка роли перед подготовкой и dispatch, запрет новых policy admissions на время CAS, неизменные issuer/owner, реальный административный пользователь и причина в журнале. Policy и точная версия установки коммитятся одним foundation batch; успешный ответ выдаётся после receipt. Обычные операции владельца не получают этот путь.

Журнал хранит последние 128 native command transitions без аргументов, текста файлов и логов программы. `repro` выдаёт ограниченный JSON с build/catalog/verifier identity, hash, памятью/gas/событиями и страницей журнала. Исходники и bytecode не включаются; доступ к закрытому source остаётся под обычным ACL.

## Blob storage

Логические квоты проекта/устройства проверяются независимо от дедупликации. Физическое хранилище дополнительно ограничено 262144 уникальными blobs и 4 GiB bytes. Общий admission: 8 новых blobs/s с burst 128; 256 KiB/s с burst 4 MiB. Счётчики, токены и вставка находятся в одной serializable transaction. Повторное сохранение известного digest не расходует токены, но продлевает 24-часовой срок для создания ссылки. Недостаток ресурсов возвращает quota failure без успешной квитанции.

До завершения старого keyset backfill счётчиков и всех current/historical/checkpoint roots сборщик не удаляет bytes. История ограничена 128 версиями и 30 днями; current snapshots, installations, publications и сохранённые checkpoints защищают свои bytes независимо. Удалённый проект сразу закрыт для чтения/изменения; после 30 дней journal/CAS retirement освобождает его авторские roots. Сборщик обрабатывает до 32 удалений за DB work unit и продолжает на следующем тике при полном batch. Ошибки и неизвестная будущая root policy запрещают очистку.

## Сборка и воспроизводимость

Для полного native прогона использовать `Tools/_NC/Meta/build-acceptance.ps1`, затем `run-integration.ps1 -BuiltDirectory <private-cohort>/bin/Content.IntegrationTests`. Новая сборка получает отдельный GUID output/intermediate. Manifest сверяет runtime/core/server/worker hashes; PE inventory исключает случайное использование сборки с вырезанными META fixtures. Ресурсы копируются в staging, hot reload выключен. Параллельные обычные диагностические сборки не должны изменять запущенный cohort.

`Content.Meta.Tests` проверяет пакеты каталога, source hashes, ABI bindings, fixtures, app contracts, каждый `.mui` и `.mstyle`. Отдельный CI workflow собирает worker и выполняет этот набор на Windows. Workflow не выгружает counterexamples или результаты во внешнее artifact storage. Изолированный compiler worker текущего профиля использует Windows Job Objects; работоспособность на другой ОС требует отдельного ограничителя и отдельной приёмки.

Нагрузочный драйвер и ограничения измерения описаны в `Tools/_NC/Meta/Performance/README.md`. Raw distributions, CPU attribution, UI transport и физическое завершение действий имеют разные измерительные границы. Их нельзя заменять друг другом или выдавать синтетический расчёт за измеренный предел.

Backup должен включать целиком foundation journal/receipts, blobs, checkpoint sections/inventory и серверную конфигурацию блокировок. SQL dump без immutable bytes или выбранного checkpoint inventory не является полным backup. Восстановление проверяется в новом изолированном окружении; рабочую БД для этой проверки не переписывают.

`Tools/_NC/Meta/validate-sqlite-backup.py` работает только с явно выбранными файлами под `artifacts/`: backup API читает источник read-only, создаёт новую копию, проверяет integrity/foreign keys и hashes исходных столбцов всех таблиц после migrations. `validate-postgres-backup.py` запускается через WSL Python для записанного owned PostgreSQL 18 cluster (`artifacts/meta-postgres-owned.json`, loopback:55432). Он создаёт новый custom dump и новую DB, сравнивает все durable rows, полную visible schema, constraints и sequence state. Он не переписывает и не удаляет существующие БД. Physical column-number gaps после старых DROP COLUMN не относятся к logical backup contract; порядок всех видимых columns проверяется. Отчёты содержат counts/hashes, без содержимого строк. Эти инструменты проверяют БД; gameplay cold boot и серверная конфигурация требуют отдельного восстановления.

## Готовые приложения и получение оборудования / Ready applications and equipment

Каталог `Resources/Meta/Projects/catalog.json` содержит нормативные пятнадцать примеров, библиотеку, приложения мастерской и панели, дополнительные доменные примеры, два stock-приложения и пять учебных этапов. Каждый новый stock/tutorial пакет поставляет META, `app.json`, реальный MUI, manifest с hashes и изолированный fixture. Generic template tests независимо компилируют, проверяют exports, contracts, markup/styles и ожидаемые команды; это не заменяет физическую приёмку.

Native меню хоста открывает installer/recovery/hardware/diagnostics без исходного редактора. `PrepareStockApplication` клонирует только доверенный stock/tutorial ресурс в личный проект и запускает обычный compiler worker; установка, платёж, bindings и service consent остаются отдельными штатными действиями. Tools projection не отправляет source chunks. Для возврата в IDE используется явная операция `OpenNativeTools` с `Source=editor` либо обычный native development verb. Клиент очищает редакторский буфер при входе в Tools.

The installer never runs inside guest META. A purchased closed release uses the existing license resolver, exact target/slot and explicit confirmation; no source-hash endpoint or automatic grants are added. Recovery selects an exact installation. Hardware data comes from actual host state and scheduler usage. The private security-journal application is separate from protected native NET evidence. The door application distinguishes an accepted request from its observed physical phase.

Обычный `VendomatInventory` поставляет два `NCMetaCommissioningKit`, поэтому комплект доступен через уже размещённые Vendomat без изменения production-карт. В комплекте конечные физические tablet, sensor/alarm/panel flatpacks, четыре кабеля данных, две батареи, ключ, мультитул для распаковки, инструмент настройки и руководство. Существующие native construction/vehicle catalogs также получают конечные определения совместимого оборудования; stock vendor использует тот же конечный механизм vending. Покупка не устанавливает имплант хирургически и не предоставляет META ownership/control.

Учебные пакеты: threshold 30 °C → hysteresis 30/25 °C → атомарный `training/last-transition.txt` → bounded MUI limits → native install/stop/recover. Bindings: `training-sensor`, `training-alarm`, затем `training-data` на FileStore своего хоста. Форма меняет globals текущей VM, а не постоянные настройки. Нужны свежий sample, физическая связь, питание и явно выданные методы; температура меняется штатным обогревателем и атмосферой. EN/RU руководство открывается из физического `NCMetaProgrammingManual`.

## Изолированные физические сцены / Isolated physical scenes

`ncMetaDemoScene` описывает шесть отдельных ограниченных fixtures. Доверенный `NCMetaDemoSceneSystem` создаёт отдельную карту и grid, реальные пол/воздух/LV cables, конечный 200 kJ APC, освещение, оборудование и native data cables. Максимум восемь сцен; удаляется только созданная сцена. Factory не выдаёт ownership/service grants, не назначает пациента, не включает automation и не загружает software. Появившиеся waypoint entities ещё не входят в mission map.

| Scene | Практическая настройка после создания |
| --- | --- |
| `NCMetaDoorScene` | Зарегистрировать host, sensor, alarm, door, panel и camera. Собирать/включать heater штатно ключом и меню. Учебный FileStore — host; panel связывается с door обычной настройкой. |
| `NCMetaGarageScene` | Зарегистрировать host/ECU, использовать настоящие scanner/lift/tools, ключ, топливо и физические service условия. Приложение garage использует существующие service orders; сцена не создаёт счета или оплату. |
| `NCMetaNetScene` | Зарегистрировать узлы и deck, подключиться к AP, на owned control node явно выбрать reachable door с разрешённым service control. Data path заполняется обычным FileStore. Probe/ICE/trace/disconnect остаются native NET. |
| `NCMetaPatrolDeliveryScene` | Зарегистрировать drone и инструментом добавить физические checkpointA/checkpointB; delivery использует назначенное имя loading. Выбрать режим и grant. Camera/gripper работают с настоящими предметами, препятствиями и контейнерами. |
| `NCMetaConveyorScene` | Зарегистрировать conveyor/sensor/fabricator, привязать cell инструментом, загрузить настоящий материал и включить automation. Ручное вмешательство/потеря питания останавливает операции. |
| `NCMetaBedsideLabScene` | Привязать пациента явно; scanner/table связать обычным NetworkConfigurator, поместить того же пациента на table. Для grinder загрузить ингредиент и beaker; ChemMaster buffer готовится отдельно, нужен штатный practitioner. |

TOOLS+NC_DIAGNOSTICS предоставляет `meta_demo_create {scene}`, `meta_demo_list`, `meta_demo_status {fixtureId}`, `meta_demo_destroy {fixtureId}` через существующую fixture-admin защиту и generation-checked diagnostic envelope. Create возвращает объекты для обычного `fixture_attach`/UI, но не даёт городскому NPC новую модель существования. Все тела здесь относятся к отдельному временному acceptance fixture.

Шесть `Tools/_NC/SS14Mcp/Scenarios/meta-physical-*.json` создают реальное оборудование, ждут фактическое питание/маршрут и удаляют fixture. Новый Tools allowlist требует согласованной пересборки/перезапуска MCP. `NCMetaDemoSceneTest` дополнительно проверяет реальный расход батареи, потерю cable/power, отсутствие автоматических grants/mission/patient и физическое содержимое продаваемого kit. Тесты и GUI этой source-партии должны быть выполнены на свежем immutable cohort; до результатов они не считаются PASS.
