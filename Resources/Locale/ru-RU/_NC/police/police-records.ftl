# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

ent-NCComputerPoliceRecords = терминал записей NCPD
    .desc = Защищённый терминал постоянных полицейских досье Найт-Сити.
ent-NCPoliceRecordsComputerCircuitboard = плата терминала записей NCPD
    .desc = Компьютерная плата для терминала записей NCPD.

nc-police-records-title = Записи NCPD
nc-police-records-search-placeholder = Поиск по полному или частичному имени...
nc-police-records-search = Найти
nc-police-records-results = Жители
nc-police-records-present-group = В ТЕКУЩЕМ ЗАПУСКЕ
nc-police-records-registry-group = ГОРОДСКОЙ РЕЕСТР
nc-police-records-no-results = Подходящие жители не найдены
nc-police-records-select-prompt = Выберите жителя, чтобы открыть его досье.
nc-police-records-job = Место работы: 
nc-police-records-current-status = Полицейский статус: 
nc-police-records-current-reason = Основание: 
nc-police-records-updated-by = Последнее изменение: 
nc-police-records-change-status = Изменить полицейский статус
nc-police-records-reason-placeholder = Укажите IC-основание для решения...
nc-police-records-apply = Применить и записать
nc-police-records-history = История действий
nc-police-records-no-history = Полицейских действий пока не зарегистрировано.
nc-police-records-history-line = {$time} | {$actor}: {$oldStatus} -> {$newStatus}. {$reason}
nc-police-records-error = Сеть записей NCPD не смогла обработать запрос.
nc-police-records-access-denied = Ваша ID-карта не даёт доступа к этому терминалу NCPD.

nc-police-status-none = Нет активного статуса
nc-police-status-questioning = Требуется для допроса
nc-police-status-suspected = Подозреваемый
nc-police-status-wanted = Разыскивается
nc-police-status-detained = Задержан
nc-police-status-arrested = Арестован
nc-police-status-imprisoned = Заключён
nc-police-status-paroled = Условно освобождён
nc-police-status-released = Освобождён
nc-police-status-missing = Пропал без вести
nc-police-status-dangerous = Вооружён и опасен

nc-police-tab-dossier = Досье
nc-police-tab-cases = Дела
nc-police-tab-warrants = Ордера

nc-police-cases-list = Полицейские дела
nc-police-case-title-placeholder = Название дела...
nc-police-case-create = Открыть дело на выбранного жителя
nc-police-case-select-prompt = Выберите дело, чтобы открыть его журнал.
nc-police-case-subjects = Связанные жители
nc-police-case-link-resident = Связать выбранного жителя
nc-police-case-reports = Неизменяемые рапорты
nc-police-case-add-report = Добавить рапорт
nc-police-case-status-reason = IC-основание изменения статуса...
nc-police-case-change-status = Изменить
nc-police-case-status-line = Статус: {$status}
nc-police-case-subject-line = {$name} — {$role}
nc-police-case-entry-line = {$time} | {$author}: {$text}
nc-police-case-entry-status-changed = Статус изменён: {$oldStatus} -> {$newStatus}. {$reason}
nc-police-case-entry-subject-added = Житель {$name} связан с делом как «{$role}».

nc-police-case-status-open = Открыто
nc-police-case-status-underinvestigation = Расследуется
nc-police-case-status-closed = Закрыто
nc-police-case-status-archived = В архиве
nc-police-case-role-personofinterest = Представляет интерес
nc-police-case-role-suspect = Подозреваемый
nc-police-case-role-victim = Потерпевший
nc-police-case-role-witness = Свидетель
nc-police-case-role-other = Другое

nc-police-warrants-list = Реестр ордеров
nc-police-warrant-create = Выдать ордер на выбранного жителя
nc-police-warrant-case-link = Связан с делом №{$caseId}
nc-police-warrant-no-case-link = Не связан с делом
nc-police-warrant-link-selected-case = Связать с выбранным делом
nc-police-warrant-resolve = Завершить ордер
nc-police-warrant-status-line = Статус: {$status}
nc-police-warrant-reason-line = Основание: {$reason}
nc-police-warrant-issued-line = Выдал {$actor}, {$time}
nc-police-warrant-not-resolved = Ордер всё ещё активен.
nc-police-warrant-resolution-line = Завершил {$actor}: {$reason}

nc-police-warrant-type-questioning = Допрос
nc-police-warrant-type-search = Обыск
nc-police-warrant-type-detention = Задержание
nc-police-warrant-type-arrest = Арест
nc-police-warrant-status-active = Активен
nc-police-warrant-status-executed = Исполнен
nc-police-warrant-status-revoked = Отозван
nc-police-warrant-status-expired = Истёк
