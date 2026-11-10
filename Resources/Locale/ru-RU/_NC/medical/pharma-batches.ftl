# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

ent-NCPharmaAutoinjector = автоинъектор с дозами
    .desc = Одноразовая упаковка доз препарата. На этикетке написано то, что заявляет производитель, а не то, что внутри.
    .suffix = { "" }

nc-pharma-container-examine = Партия [bold]{$code}[/bold] — {$concentration} на дозу, чистота {$purity}, {$sterile}, класс: {$grade}. Осталось доз: {$doses}.
nc-pharma-label-sterile = стерильно
nc-pharma-label-not-sterile = не стерильно
nc-pharma-grade-street = уличный
nc-pharma-grade-medical = медицинский
nc-pharma-grade-corporate = корпоративный

nc-pharma-administer-started = Вы начинаете вводить дозу.
nc-pharma-administer-complete = Доза введена.
nc-pharma-administer-failed = Не удалось ввести дозу.

nc-pharma-delivery-oral = пероральная
nc-pharma-delivery-autoinjector = автоинъектор
nc-pharma-delivery-infusion = инфузия
nc-pharma-delivery-inhaled = ингаляционная
nc-pharma-delivery-sublingual = сублингвальная
nc-pharma-delivery-patch = трансдермальный пластырь

nc-pharma-modifier-fast-release = быстрое высвобождение
nc-pharma-modifier-extended-release = пролонгированное высвобождение
nc-pharma-modifier-protective-carrier = защитный носитель
nc-pharma-modifier-stabilizer = стабилизатор
nc-pharma-modifier-buffered = буферизованный состав
nc-pharma-modifier-concentrated = концентрат
nc-pharma-modifier-purified = очищенный состав
nc-pharma-modifier-cold-solvent = холодный растворитель

nc-pharma-production-street = быстрый синтез
nc-pharma-production-standard = стандартный синтез
nc-pharma-production-clean = чистый синтез

nc-pharma-formula-trauma-analgesic = полевой обезболивающий препарат при травмах
nc-pharma-formula-field-stimulant = полевой стимулятор
nc-pharma-formula-broad-antibiotic = инфузия антибиотика широкого спектра

ent-NCPharmaSourceCanisterStreet = канистра с прекурсором
    .desc = Сырьё для фармацевтического синтеза. Уличный товар неровного качества.
    .suffix = уличный класс

ent-NCPharmaSourceCanisterClinical = канистра с прекурсором
    .desc = Сырьё для фармацевтического синтеза. Клинический класс с документированным поставщиком.
    .suffix = клинический класс

ent-NCPharmaSynthesizerBase = фармацевтический синтезатор
    .desc = Выпускает известную формулу партиями из канистры с сырьём.

ent-NCPharmaSynthesizerStreet = фармацевтический синтезатор
    .desc = Кустарная линия. Быстрая и дешёвая, но со слабой стерильностью, смешиванием и калибровкой.
    .suffix = уличная лаборатория

ent-NCPharmaSynthesizerClinical = фармацевтический синтезатор
    .desc = Клиническая производственная линия: стерильная обработка, ровное смешивание, откалиброванная дозировка.
    .suffix = клиническая линия

nc-pharma-station-title = Фармацевтический синтезатор
nc-pharma-station-material = Сырьё
nc-pharma-station-formula = Формула
nc-pharma-station-profile = Режим производства
nc-pharma-station-units = Расход сырья:
nc-pharma-station-start = Начать производство
nc-pharma-station-source-slot = сырьё
nc-pharma-station-source-present = Канистра: {$units} ед., заявленная чистота {$purity}
nc-pharma-station-source-missing = Канистра с сырьём не вставлена.
nc-pharma-station-no-modifiers = нет
nc-pharma-station-formula-card = Версия {$version} · {$form} · {$concentration} на дозу · модификаторы: {$modifiers} · статус: {$status}
nc-pharma-station-forecast = Ожидается: [bold]{$doses}[/bold] доз, [bold]{$seconds}[/bold] с, стоимость x{$cost}
nc-pharma-station-finished = Синтезатор заканчивает партию {$code}.
nc-pharma-station-failed = Синтез не удался и ничего не выпустил.
nc-pharma-station-notice-no-source = Сначала вставьте канистру с сырьём.
nc-pharma-station-notice-not-enough = В канистре не хватает сырья.
nc-pharma-station-notice-invalid = Запрос отклонён.
nc-pharma-station-notice-busy = Производство уже идёт.
nc-pharma-station-notice-started = Производство начато.
nc-pharma-station-notice-finished = Производство завершено.
nc-pharma-station-notice-failed = Производство не удалось.

nc-pharma-status-unknown = неизвестен
nc-pharma-status-free-sale = свободная продажа
nc-pharma-status-prescription = рецептурный
nc-pharma-status-controlled = контролируемый
nc-pharma-status-clinical-only = только клиническое применение
nc-pharma-status-corporate = корпоративный
nc-pharma-status-banned = запрещён
nc-pharma-status-recalled = отозван

nc-pharma-qc-untested = не проверена
nc-pharma-qc-partial = проверена частично
nc-pharma-qc-certified = сертифицирована
nc-pharma-qc-rejected = отклонена
nc-pharma-qc-recalled = отозвана

ent-NCPharmaQcAnalyzer = фармацевтический QC-анализатор
    .desc = Измеряет, что на самом деле содержит партия. Каждый запуск расходует одну дозу вставленной упаковки как образец.

nc-pharma-container-examine-qc = Лабораторный статус: {$state}.
nc-pharma-qc-title = Фармацевтический QC-анализатор
nc-pharma-qc-sample = Образец
nc-pharma-qc-sample-slot = упаковка-образец
nc-pharma-qc-sample-info = Партия [bold]{$code}[/bold] · осталось доз: {$doses} · статус: {$state}
nc-pharma-qc-sample-missing = Вставьте упаковку с дозами для анализа.
nc-pharma-qc-tests = Какие тесты провести
nc-pharma-qc-test-concentration = концентрация
nc-pharma-qc-test-purity = чистота
nc-pharma-qc-test-sterility = стерильность
nc-pharma-qc-test-homogeneity = однородность
nc-pharma-qc-test-stability = стабильность
nc-pharma-qc-run = Запустить анализ (расходует одну дозу)
nc-pharma-qc-result = Результат
nc-pharma-qc-no-result = Анализа этой упаковки ещё не было.
nc-pharma-qc-row-passed = [color=#67E89A]{$name}[/color]: этикетка {$label} · измерено {$value}
nc-pharma-qc-row-failed = [color=#FF6170]{$name}[/color]: этикетка {$label} · измерено {$value} — [bold]не соответствует[/bold]
nc-pharma-qc-row-not-tested = [color=#7898A0]{$name}[/color]: не проверялось
nc-pharma-qc-finished = Анализатор заканчивает проверку партии {$code}.
nc-pharma-qc-notice-no-sample = Сначала вставьте упаковку с дозами.
nc-pharma-qc-notice-no-dose = В упаковке не осталось доз для образца.
nc-pharma-qc-notice-unknown-batch = Упаковка не относится ни к одной известной партии.
nc-pharma-qc-notice-no-tests = Выберите хотя бы один тест.
nc-pharma-qc-notice-busy = Анализ уже идёт.
nc-pharma-qc-notice-started = Анализ начат.
nc-pharma-qc-notice-finished = Анализ завершён.
nc-pharma-formula-maintenance-analgesic = поддерживающий анальгетик

nc-pharma-formula-euphoric = клинический эйфорик
nc-pharma-formula-social = социальный стимулятор
ent-NCPharmaSynthesizerCorporate = фармацевтический синтезатор
    .desc = Корпоративная производственная линия: стерильна на всех этапах, точная дозировка и большой объём партии.
    .suffix = корпоративная лаборатория

nc-pharma-phase-onset = { $effect ->
   *[stimulation] Пульс учащается, мысли становятся острее.
    [sedation] Вас начинает накрывать тяжёлое спокойствие.
    [euphoria] По телу расходится тёплая волна благополучия.
    [perception] Края мира начинают смещаться.
    [sociability] Вы чувствуете себя свободнее и разговорчивее.
    [dissociation] Вы начинаете ощущать дистанцию от собственного тела.
}
nc-pharma-phase-peak = { $effect ->
   *[stimulation] Вы на взводе, быстры и совершенно бодры.
    [sedation] Вы глубоко и тяжело спокойны.
    [euphoria] Всё кажется тёплым, лёгким и хорошим.
    [perception] Цвета и звуки странные и яркие.
    [sociability] Вам легко со всеми вокруг.
    [dissociation] Вы наблюдаете за собой издалека.
}
nc-pharma-phase-decline = { $effect ->
   *[stimulation] Острота понемногу уходит.
    [sedation] Спокойствие редеет.
    [euphoria] Тепло медленно угасает.
    [perception] Странность понемногу оседает.
    [sociability] Лёгкость настроения ускользает.
    [dissociation] Вы чувствуете, что возвращаетесь.
}
nc-pharma-phase-comedown = { $effect ->
   *[stimulation] Подъём прошёл, вы чувствуете опустошение.
    [sedation] Вы вялы и заторможены.
    [euphoria] Тепло ушло, вы чувствуете пустоту и усталость.
    [perception] Мир кажется тусклым, вы измотаны.
    [sociability] Настроение падает, вы устали и раздражены.
    [dissociation] Вы вернулись, но чувствуете тяжесть и онемение.
}

nc-pharma-production-manual = ручной синтез
nc-pharma-reaction-stable = стабильная реакция
nc-pharma-reaction-exothermic = экзотермическая реакция
nc-pharma-reaction-volatile = летучая реакция
nc-reagent-name-reactor-fumes = испарения реактора
nc-reagent-desc-reactor-fumes = Едкие испарения реакции, вышедшей из-под контроля. Повреждают лёгкие.
ent-NCPharmaReactor = термореактор
nc-pharma-reactor-title = Термореактор
nc-pharma-reactor-section-run = Запуск
nc-pharma-reactor-section-gauges = Показания
nc-pharma-reactor-section-controls = Управление
nc-pharma-reactor-start = Начать синтез
nc-pharma-reactor-no-run = Синтез не идёт.
nc-pharma-reactor-windows = Оптимум [bold]{$optimalMin}-{$optimalMax}[/bold] °C · безопасно {$safeMin}-{$safeMax} °C · давление мягкий предел {$soft} / жёсткий {$hard}
nc-pharma-reactor-temperature = Температура: {$value} °C {$trend}
nc-pharma-reactor-pressure = Давление: {$value}
nc-pharma-reactor-mixing = Перемешивание {$value} · катализатор {$catalyst} (зарядов осталось: {$left})
nc-pharma-reactor-progress = Прогресс реакции: {$value}
nc-pharma-reactor-instability = Нестабильность: {$value}
nc-pharma-reactor-heater = Нагрев
nc-pharma-reactor-cooler = Охлаждение
nc-pharma-reactor-stirring = Мешалка
nc-pharma-reactor-venting = Клапан сброса давления (замедляет реакцию)
nc-pharma-reactor-ventilation = Вентиляция (уменьшает токсичный выброс)
nc-pharma-reactor-catalyst = Добавить катализатор
nc-pharma-reactor-quench = Закалка
nc-pharma-reactor-stop = Аварийный стоп
nc-pharma-reactor-warning-high-temperature = [color=#FFD05E]Температура выше оптимального окна.[/color]
nc-pharma-reactor-warning-near-limit = [color=#FF6170]Температура близка к безопасному пределу![/color]
nc-pharma-reactor-warning-high-pressure = [color=#FF6170]Давление приближается к пределу.[/color]
nc-pharma-reactor-warning-poor-mixing = [color=#FFD05E]Плохое перемешивание создаёт перегретые зоны.[/color]
nc-pharma-reactor-warning-runaway = [color=#FF6170]Реакция идёт вразнос: охладите её немедленно.[/color]
nc-pharma-reactor-warning-instability = [color=#FF6170]Смесь становится нестабильной.[/color]
nc-pharma-reactor-notice-not-experimental = Вручную можно делать только формулу, находящуюся в разработке.
nc-pharma-reactor-notice-units-limit = Ручной синтез ограничен небольшим количеством сырья.
nc-pharma-reactor-notice-started = Реакция началась.
nc-pharma-reactor-notice-finished = Синтез завершён.
nc-pharma-reactor-notice-stopped = Аварийный стоп: сырьё потеряно.
nc-pharma-reactor-notice-quenched = Реакцию закалили и остановили.
nc-pharma-reactor-notice-degraded = Смесь превратилась в шлак.
nc-pharma-reactor-notice-released = Произошёл выброс токсичных испарений.
nc-pharma-reactor-notice-ruptured = Сосуд разрушился.
nc-pharma-reactor-notice-catalyst = Катализатор добавлен.
nc-pharma-reactor-notice-no-catalyst = Катализатор закончился.
nc-pharma-reactor-popup-warning = Сигнал тревоги реактора: смесь становится нестабильной!
nc-pharma-reactor-popup-degraded = Смесь разлагается в шлак!
nc-pharma-reactor-popup-release = Реактор выбрасывает токсичные испарения!
nc-pharma-reactor-popup-rupture = Сосуд реактора разрывается и вспыхивает!
nc-pharma-reactor-popup-severe = Сосуд реактора сильно взрывается!
nc-pharma-reactor-popup-finished = Реактор заканчивает партию {$code}.

ent-NCPharmaColdStorage = фармацевтическое холодное хранилище
    .desc = Шкаф с питанием, который держит лекарства холодными. Нестабильные партии не портятся внутри, пока у шкафа есть питание.
nc-pharma-container-examine-keep-cold = [color=#7DBDB7]Хранить в холоде: эта формула портится при комнатной температуре.[/color]

# Консоль исследований и журнал.
nc-pharma-rd-title = Консоль исследования формул
nc-pharma-rd-formulas = Формулы
nc-pharma-rd-selected = Выбранная формула
nc-pharma-rd-new-version = Новая версия
nc-pharma-rd-name-placeholder = Название новой версии
nc-pharma-rd-delivery = Форма доставки
nc-pharma-rd-concentration = Концентрация
nc-pharma-rd-modifier-first = Первый модификатор
nc-pharma-rd-modifier-second = Второй модификатор
nc-pharma-rd-no-modifier = нет
nc-pharma-rd-create = Создать экспериментальную версию
nc-pharma-rd-approve = Утвердить выбранную
nc-pharma-rd-journal = Лабораторный журнал
nc-pharma-rd-journal-empty = Журнал пуст.
nc-pharma-rd-select-hint = Выберите формулу, чтобы увидеть её карточку и разработать новую версию.
nc-pharma-rd-candidate = кандидат
nc-pharma-rd-formula-title = {$name} v{$version} ({$status})
nc-pharma-rd-card = {$name} · версия {$version} · {$form} · {$concentration} на дозу · модификаторы: {$modifiers} · статус: {$status}
nc-pharma-rd-forecast = Прогноз: пик {$peak} · запас до токсичности {$margin} · всасывание {$seconds} с · токсичность ×{$toxicity} · стоимость ×{$cost}
nc-pharma-rd-journal-row = [{$kind}] {$formula} · партия {$batch} · {$actor} · {$detail}
nc-pharma-rd-notice-created = Создана новая экспериментальная версия.
nc-pharma-rd-notice-approved = Формула утверждена.
nc-pharma-rd-notice-invalid = Запрос недопустим для этой формулы.
nc-pharma-rd-notice-invalid-name = Название пустое, слишком длинное или содержит запрещённые символы.
nc-pharma-rd-notice-too-many = Лаборатория не может хранить больше производных формул.
nc-pharma-rd-notice-not-experimental = Утвердить можно только экспериментальную версию.
nc-pharma-rd-notice-not-certified = Эта версия ещё не сертифицирована контролем качества.
nc-pharma-rd-notice-unidentified = Соединение ещё не определено химическим анализом.
nc-pharma-journal-produced = выпуск
nc-pharma-journal-qc = контроль качества
nc-pharma-journal-administered = введение
nc-pharma-journal-approved = утверждение
nc-pharma-journal-version-created = версия создана
nc-pharma-station-formula-title = {$name} v{$version}
nc-pharma-status-experimental = экспериментальная
nc-pharma-phase-none = нет
