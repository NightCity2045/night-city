# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

ent-NCPropertyMarker = отметка помещения
    .desc = Маркер маппинга для сдаваемого помещения: задайте ID, название и плату за час.
ent-NCRealEstateTerminal = терминал агентства недвижимости
    .desc = Квартиры, гаражи и коммерческие помещения в аренду. Оплатил, получил карты, заехал.
ent-NCPropertyKeyCard = ключ-карта
    .desc = Пластиковая ключ-карта от арендованной двери.

nc-property-card-name = ключ-карта: {$property}, дверь {$door}/{$doors}
nc-property-card-examine = Открывает дверь помещения [color=yellow]{$property}[/color], пока действует аренда, под которую её выпустили.
nc-property-door-locked = Заперто. Нужна ключ-карта от этой двери.
nc-property-expiring = Аренда «{$property}» закончится через 15 минут. Продлите её в агентстве недвижимости.
nc-property-evicted = Аренда «{$property}» закончилась. Замки сменили.

nc-property-rented = Договор подписан. Ключ-карты у вас в руках.
nc-property-extended = Аренда продлена.
nc-property-cancelled = Аренда расторгнута. Замки сменили.
nc-property-error-closed = Агентство сейчас не работает.
nc-property-error-unknown = Этого помещения нет на рынке.
nc-property-error-term = Агентство не сдаёт на такой срок.
nc-property-error-no-account = Для аренды нужен банковский счёт.
nc-property-error-taken = Его уже кто-то снял.
nc-property-error-funds = На счёте недостаточно денег.
nc-property-error-not-yours = Это не ваша аренда.

nc-real-estate-title = Агентство недвижимости
nc-real-estate-balance = Счёт:
nc-real-estate-money = {$amount} €$
nc-real-estate-mine-only = Только мои
nc-real-estate-hint = Аренда оплачивается вперёд в часах городского времени. Когда срок выйдет, замки сменят; оставленное внутри останется там.
nc-real-estate-closed = Агентство не работает.
nc-real-estate-no-account = Для аренды нужен банковский счёт.
nc-real-estate-empty = Нечего показать.
nc-real-estate-place = {$district}, {$building}, этаж {$floor}
nc-real-estate-unknown = неизвестно
nc-real-estate-details = {$area} клеток · {$doors ->
    [one] {$doors} дверь
    [few] {$doors} двери
    [many] {$doors} дверей
   *[other] {$doors} двери
} · {$rent} €$/ч
nc-real-estate-status-vacant = Свободно
nc-real-estate-status-rented = Сдано
nc-real-estate-status-mine = Ваша аренда · осталось {$time}
nc-real-estate-rent = Снять на:
nc-real-estate-extend = Продлить на:
nc-real-estate-term = {$hours} ч · {$cost} €$
nc-real-estate-cancel = Расторгнуть аренду
