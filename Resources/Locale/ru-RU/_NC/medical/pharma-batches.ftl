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

nc-pharma-modifier-fast-release = быстрое высвобождение
nc-pharma-modifier-extended-release = пролонгированное высвобождение
nc-pharma-modifier-protective-carrier = защитный носитель
nc-pharma-modifier-stabilizer = стабилизатор

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
