# SPDX-FileCopyrightText: 2026 Astro
# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
# SPDX-FileComment: Community Funding Additional Permission applies; see COMMUNITY-FUNDING-PERMISSION.md.

ncui-lab-title = Лаборатория NCUI
ncui-lab-metrics-waiting = Метрики прогреваются…
ncui-lab-tool-stroke = Инструмент линии
ncui-lab-tool-selection = Инструмент выделения
ncui-lab-baseline-enable = Включить baseline из 10 000 Control
ncui-lab-baseline-disable = Вернуться к поверхности NCUI
ncui-lab-simulate-user = Симулировать второго участника
ncui-lab-remove-user = Удалить второго участника
ncui-lab-simulate-resync = Симулировать разрыв ревизий
ncui-lab-virtual-list-heading = 50 000 виртуальных записей
ncui-lab-inspector-heading = Инспектор выделения
ncui-lab-inspector-empty = Выберите объект поверхности.
ncui-lab-event-empty = Включите ncui_debug_events для просмотра маршрута.
ncui-lab-record = Запись {$index}
ncui-lab-inspector-value = Узел {$id}, позиция {$x}, {$y}; выделено: {$count}
ncui-lab-event-value = {$type} → узел {$id}; обработано: {$handled}
ncui-lab-resync-value = Обнаружен искусственный разрыв; запрошен снимок на ревизии {$revision}.
ncui-lab-metrics = Control: {$controls} | Узлы: {$nodes} | Видимые: {$visible} | Интерактивные: {$interactive} | Строки: {$rows} | Draw: {$draw} мс | Hit: {$hit} мс | Аллокации: {$allocations} Б | Ревизия: {$revision}

ncui-debug-enabled = {$command}: включено
ncui-debug-disabled = {$command}: выключено
ncui-debug-overlay-metrics =
    Сессия {$session}; ревизия {$revision}
    Узлы {$nodes} / {$visible} видимых / {$interactive} интерактивных
    Ячейки {$buckets}; вызовы draw {$drawCommands}; анимации {$animations}
    Hit {$hit} мс; update {$update} мс; draw {$draw} мс; patch {$patch} мс
    Ожидают {$pending}; сеть {$networkIn}/{$networkOut} КиБ/с; аллокации {$allocations} Б/кадр

cmd-ncui_lab-desc = Открывает лабораторию производительности и взаимодействия NCUI.
cmd-ncui_debug_bounds-desc = Переключает границы логических узлов.
cmd-ncui_debug_hitboxes-desc = Переключает интерактивные hitbox.
cmd-ncui_debug_spatial-desc = Переключает занятые ячейки пространственной сетки.
cmd-ncui_debug_events-desc = Переключает диагностику маршрутизации событий NCUI.
cmd-ncui_debug_network-desc = Переключает сетевую диагностику NCUI.
cmd-ncui_debug_layout-desc = Переключает диагностику layout NCUI.
cmd-ncui_debug_allocations-desc = Переключает измерение покадровых аллокаций NCUI.
cmd-ncui_debug_delay-desc = Задерживает входящие ответы NCUI для проверки отказоустойчивости.
cmd-ncui_debug_reject_next-desc = Принудительно проводит следующее действие NCUI через локальный отказ.
cmd-ncui_debug_drop_patch-desc = Теряет следующий патч NCUI для проверки разрыва и resync.
