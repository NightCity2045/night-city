# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

nc-reagent-name-trauma-analgesic = травматологический анальгетик
nc-reagent-desc-trauma-analgesic = Подавляет восприятие боли, не устраняя саму травму.
nc-reagent-name-surgical-anesthetic = хирургический анестетик
nc-reagent-desc-surgical-anesthetic = Создаёт управляемую анестезию; передозировка угнетает дыхание.
nc-reagent-name-sedative = клинический седатив
nc-reagent-desc-sedative = Снижает бодрствование и подвижность, не излечивая повреждения.
nc-reagent-name-vasopressor = вазопрессор
nc-reagent-desc-vasopressor = Поддерживает сердечный выброс и сосудистый тонус при реанимации.
nc-reagent-name-antiarrhythmic = антиаритмик
nc-reagent-desc-antiarrhythmic = Стабилизирует нестабильный ритм, но не запускает остановившееся сердце.
nc-reagent-name-respiratory-stimulant = стимулятор дыхания
nc-reagent-desc-respiratory-stimulant = Усиливает работу функционирующих лёгких, но не заменяет отсутствующие.
nc-reagent-name-broad-antibiotic = антибиотик широкого спектра
nc-reagent-desc-broad-antibiotic = Подавляет восприимчивую инфекцию раны при терапевтической концентрации.
nc-reagent-name-antitoxin = системный антитоксин
nc-reagent-desc-antitoxin = Ускоряет выведение токсической нагрузки работающими органами.
nc-reagent-name-immunosuppressant = иммунодепрессант
nc-reagent-desc-immunosuppressant = Подавляет отторжение трансплантата, повышая уязвимость к инфекции.
nc-reagent-name-procoagulant = прокоагулянт
nc-reagent-desc-procoagulant = Снижает кровотечение системной поддержкой свёртывания, не закрывая рану.
nc-reagent-name-stimulant = клинический стимулятор
nc-reagent-desc-stimulant = Повышает бодрствование и сердечно-лёгочную активность; передозировка дестабилизирует физиологию.
nc-reagent-name-anticoagulant = антикоагулянт
nc-reagent-desc-anticoagulant = Снижает свёртывание и усиливает кровотечение, пока сохраняется терапевтическая концентрация.
nc-reagent-name-vasodilator = вазодилататор
nc-reagent-desc-vasodilator = Снижает сосудистый тонус; опасная доза может обрушить перфузию.
nc-reagent-name-synthetic-oxygen-carrier = синтетический переносчик кислорода
nc-reagent-desc-synthetic-oxygen-carrier = Временно добавляет циркулирующей жидкости кислородную ёмкость, не восполняя потерю объёма крови.
nc-reagent-name-cyberware-coolant-agent = охлаждающий агент для киберимплантов
nc-reagent-desc-cyberware-coolant-agent = Циркулирует через совместимые импланты и отводит измеримый избыток тепла; органические ткани не восстанавливает.

# Предзаполненные шприцы

ent-NCSyringeTraumaAnalgesic = автоинъектор травматологического анальгетика
    .desc = { nc-pharma-formula-trauma-analgesic-desc }

ent-NCSyringeSurgicalAnesthetic = капельница с хирургическим анестетиком
    .desc = { nc-pharma-formula-surgical-anesthetic-desc }

ent-NCSyringeSedative = капельница с седативным
    .desc = { nc-pharma-formula-sedative-desc }

ent-NCSyringeVasopressor = автоинъектор вазопрессора
    .desc = { nc-pharma-formula-vasopressor-autoinjector-desc }

ent-NCSyringeAntiarrhythmic = капельница с антиаритмиком
    .desc = { nc-pharma-formula-antiarrhythmic-desc }

ent-NCSyringeRespiratoryStimulant = автоинъектор респираторного стимулятора
    .desc = { nc-pharma-formula-respiratory-stimulant-desc }

ent-NCSyringeBroadAntibiotic = капельница с антибиотиком широкого спектра
    .desc = { nc-pharma-formula-broad-antibiotic-desc }

ent-NCSyringeAntitoxin = автоинъектор антитоксина
    .desc = { nc-pharma-formula-antitoxin-desc }

ent-NCSyringeImmunosuppressant = капельница с иммуносупрессантом
    .desc = { nc-pharma-formula-immunosuppressant-desc }

ent-NCSyringeProcoagulant = автоинъектор прокоагулянта
    .desc = { nc-pharma-formula-procoagulant-desc }

ent-NCSyringeStimulant = автоинъектор полевого стимулятора
    .desc = { nc-pharma-formula-field-stimulant-desc }

ent-NCSyringeAnticoagulant = капельница с антикоагулянтом
    .desc = { nc-pharma-formula-anticoagulant-desc }

ent-NCSyringeVasodilator = капельница с вазодилататором
    .desc = { nc-pharma-formula-vasodilator-desc }

ent-NCSyringeSyntheticOxygenCarrier = пакет синтетической крови
    .desc = { nc-pharma-formula-synthetic-blood-desc }

ent-NCSyringeCyberwareCoolantAgent = шприц с охлаждающим агентом для киберимплантов
    .desc = { nc-reagent-desc-cyberware-coolant-agent }
    .suffix = { "" }

ent-NCSyringeStimulantStreet = уличный полевой стимулятор (автоинъектор)
    .desc = Сварено в подсобке. Этикетка оптимистична.
    .suffix = уличного качества
ent-NCSyringeTraumaAnalgesicStreet = уличный травматический анальгетик (автоинъектор)
    .desc = Сварено в подсобке. Этикетка оптимистична.
    .suffix = уличного качества
ent-NCSyringeProcoagulantStreet = уличный прокоагулянт (автоинъектор)
    .desc = Сварено в подсобке. Этикетка оптимистична.
    .suffix = уличного качества
ent-NCSyringeAntitoxinStreet = уличный антитоксин (автоинъектор)
    .desc = Сварено в подсобке. Этикетка оптимистична.
    .suffix = уличного качества
ent-NCSyringeVasopressorStreet = уличный вазопрессор (автоинъектор)
    .desc = Сварено в подсобке. Этикетка оптимистична.
    .suffix = уличного качества
ent-NCSyringeSedativeStreet = уличное седативное (капельница)
    .desc = Сварено в подсобке. Этикетка оптимистична.
    .suffix = уличного качества
