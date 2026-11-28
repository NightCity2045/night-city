# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

## Services and programs

nc-emergency-service-ncpd = NCPD
nc-emergency-service-trauma = Trauma Team
nc-emergency-program-name = Экстренный вызов
nc-dispatch-program-name = Диспетчер

## Incidents

nc-dispatch-notification-header = Диспетчер: {$service}
nc-dispatch-notification-new = #{$id} {$kind}: {$location}, этаж {$z}. {$message}
nc-dispatch-kind-call = Вызов
nc-dispatch-kind-biomonitor = Биомонитор
nc-dispatch-kind-shooting = Стрельба
nc-dispatch-location-building = {$building}, {$district}
nc-dispatch-location-unknown = место не определено
nc-dispatch-map-marker = #{$id} {$kind}
nc-dispatch-message-empty = без подробностей
nc-dispatch-caller-sensor = датчик выстрелов
nc-dispatch-shooting-message = Зафиксирована стрельба.
nc-dispatch-biomonitor-critical = Биомонитор: критическое состояние.
nc-dispatch-biomonitor-dead = Биомонитор: признаков жизни нет.

## Caller messages

nc-dispatch-call-sent = Вызов #{$id} отправлен.
nc-dispatch-call-sent-uncovered = Вызов #{$id} отправлен. Вы вне зоны патрулирования: быстрого ответа не ждите.
nc-dispatch-call-unavailable = Служба недоступна.
nc-dispatch-call-no-signal = Здесь нет связи.
nc-dispatch-call-already = У вас уже есть активный вызов в эту службу.
nc-dispatch-call-cooldown = Подождите, прежде чем звонить снова.
nc-dispatch-call-no-account = У вас нет банковского счёта.
nc-dispatch-call-no-money = На счёте не хватает денег.
nc-dispatch-accepted = Вызов #{$id} принят: едет {$responder}.
nc-dispatch-arrived = Вызов #{$id}: экипаж на месте.
nc-dispatch-closed = Вызов #{$id} закрыт.
nc-dispatch-closed-expired = Вызов #{$id} закрыт: никто не ответил вовремя.
nc-dispatch-closed-cancelled = Вызов #{$id} отменён.
nc-dispatch-refund = Вызов #{$id}: возвращено {$amount} €$.

## Dispatcher program

nc-dispatch-unavailable = Программа для сотрудников NCPD и Trauma Team.
nc-dispatch-empty = Активных вызовов нет.
nc-dispatch-title = {$service}: вызовы
nc-dispatch-status-open = ждёт
nc-dispatch-status-accepted = принял {$responder}
nc-dispatch-status-on-scene = на месте
nc-dispatch-row-title = #{$id} {$kind}
nc-dispatch-row-age = {$minutes} мин
nc-dispatch-row-location = {$location}, этаж {$z}
nc-dispatch-row-shots = Выстрелов: {$shots}
nc-dispatch-row-caller = {$caller}: {$message}
nc-dispatch-row-plan = Тариф: {$plan}
nc-dispatch-row-uncovered = Вне зоны патрулирования
nc-dispatch-accept = Принять
nc-dispatch-close = Закрыть
nc-dispatch-not-found = Вызова больше нет.
nc-dispatch-already-taken = Этот вызов уже приняли.

## Emergency program

nc-emergency-balance = Баланс счёта: {$balance}
nc-emergency-message-placeholder = Что случилось?
nc-emergency-call = Вызвать
nc-emergency-cancel = Отменить
nc-emergency-price = Цена вызова: {$price}
nc-emergency-free = Вызов бесплатный
nc-emergency-status-open = Вызов #{$id}: ждём экипаж.
nc-emergency-status-accepted = Вызов #{$id}: едет {$responder}.
nc-emergency-status-on-scene = Вызов #{$id}: экипаж на месте.
nc-emergency-cooldown = Следующий вызов через {$seconds} с.

## Trauma Team subscription

nc-trauma-subscription-title = Подписка Trauma Team
nc-trauma-current-plan = Ваш тариф: {$plan}, {$price} каждую выплату.
nc-trauma-no-plan = Подписки нет. Бесплатно Trauma Team выезжает только к подписчикам; разовый вызов оплачивается сразу.
nc-trauma-unsubscribe = Отменить подписку
nc-trauma-plan-line = {$plan}: {$price}, прибытие за {$minutes} мин
nc-trauma-plan-active = Активен
nc-trauma-subscribe = Оформить
nc-trauma-subscribed = Trauma Team: тариф {$plan} подключён.
nc-trauma-unsubscribed = Подписка Trauma Team отменена.
nc-trauma-already-subscribed = У вас уже этот тариф.
nc-trauma-billed = Trauma Team: тариф {$plan}, -{$amount} €$.
nc-trauma-lapsed = Trauma Team: не хватило денег на тариф {$plan}. Подписка отменена.
nc-trauma-plan-silver = Серебряный
nc-trauma-plan-silver-desc = Биомонитор, выезд экипажа и стабилизация на месте.
nc-trauma-plan-gold = Золотой
nc-trauma-plan-gold-desc = Экипаж быстрее, эвакуация в клинику.
nc-trauma-plan-platinum = Платиновый
nc-trauma-plan-platinum-desc = Самый быстрый экипаж, эвакуация даже из-под огня.

## NCPD monitoring console

ent-NCComputerPoliceMonitor = консоль наблюдения NCPD
    .desc = Полицейская сеть смотрит на город. Каждая точка — человек; насколько она свежая, зависит от района.
ent-NCPoliceMonitorComputerCircuitboard = плата консоли наблюдения NCPD
    .desc = Печатная плата консоли наблюдения NCPD.
ent-NCEmergencyCartridge = картридж экстренного вызова
    .desc = Программа для вызова NCPD и Trauma Team.
ent-NCDispatchCartridge = картридж диспетчера
    .desc = Вызовы NCPD и Trauma Team.
nc-police-monitor-title = Наблюдение NCPD за городом
nc-police-monitor-hint = ПКМ по точке: имя и должность. В OCZ, Hot Zone и Reclamation Zone покрытия нет.
nc-police-monitor-count = Видно: {$count}
nc-police-monitor-no-job = без должности
