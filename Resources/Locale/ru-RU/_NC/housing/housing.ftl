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

nc-property-duplicated = Дубликат карты готов.
nc-property-rekeyed = Замки сменили. Все старые карты больше не работают, вот новый комплект.
nc-property-listed = Аренда выставлена на перепродажу.
nc-property-unlisted = Аренда снята с продажи.
nc-property-bought = Аренда куплена. Замки сменили, ключ-карты у вас в руках.
nc-property-sold = Вашу аренду «{$property}» купили за {$price} €$. Деньги на вашем счёте.
nc-property-error-busy = По этому помещению идёт платёж. Попробуйте через минуту.
nc-property-error-door = Эта дверь не относится к помещению.
nc-property-error-price = Такая цена не подходит.
nc-property-error-own-account = Нельзя оставить аренду за своим же счётом.
nc-property-error-not-for-sale = Эта аренда не продаётся.
nc-property-error-own-lease = Эта аренда и так ваша.
nc-property-error-reserved = Эта аренда оставлена другому покупателю.
nc-property-error-seller-gone = Счёта продавца больше нет.

nc-real-estate-status-for-sale = Перепродажа аренды: {$price} €$ · оплачено ещё {$time}
nc-real-estate-buy = Купить аренду · {$price} €$ + комиссия банка
nc-real-estate-reserved = Оставлено другому покупателю
nc-real-estate-door = Дверь {$door}
nc-real-estate-duplicate = Дубликат карты · {$price} €$
nc-real-estate-rekey = Сменить замки · {$price} €$
nc-real-estate-listed = На продаже за {$price} €$
nc-real-estate-listed-for = На продаже за {$price} €$ для {$buyer}
nc-real-estate-unlist = Снять с продажи
nc-real-estate-price-placeholder = Цена
nc-real-estate-buyer-placeholder = Счёт покупателя (необязательно)
nc-real-estate-list = Выставить на перепродажу

nc-property-home-set = Теперь вы будете входить в раунд здесь.
nc-property-home-cleared = Вы будете входить в раунд как обычно.
nc-real-estate-home-on = Дом: вы входите в раунд здесь
nc-real-estate-home-off = Входить в раунд здесь
