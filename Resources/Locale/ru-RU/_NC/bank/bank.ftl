# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

nc-bank-money = {$amount} €$
nc-bank-percent = {$percent}%
nc-bank-program-name = Банк
nc-bank-cash-slot-name = Наличные

nc-bank-spawn-account = Банк Найт-Сити: ваш счёт {$number}, баланс {$balance} €$.
nc-bank-salary-paid = Зарплата: +{$amount} €$. Баланс {$balance} €$.
nc-bank-salary-missed = Зарплата: у вашего отдела не хватило денег на выплату.

nc-bank-no-cash = Сначала положите наличные в слот.
nc-bank-invalid-amount = Неверная сумма.
nc-bank-insufficient-funds = На счёте недостаточно денег.
nc-bank-deposited = Зачислено {$amount} €$ (комиссия банка {$fee} €$).
nc-bank-withdrawn = Выдано {$amount} €$.
nc-bank-transfer-success = Перевод отправлен.
nc-bank-account-not-found = Такого счёта нет.
nc-bank-same-account = Нельзя перевести на тот же счёт.

nc-bank-no-account = К вам не привязан банковский счёт.
nc-bank-offline = Банк недоступен.
nc-bank-account-label = Счёт:
nc-bank-balance-label = Баланс:
nc-bank-fee-label = Комиссия банка:
nc-bank-cash-label = Наличные в слоте:
nc-bank-cash-hint = Вставьте наличные в слот и внесите их.
nc-bank-deposit = Внести наличные
nc-bank-withdraw = Снять
nc-bank-amount-placeholder = Сумма
nc-bank-target-placeholder = Номер счёта (NC-000000)
nc-bank-transfer-title = Перевод
nc-bank-transfer = Отправить
nc-bank-transfer-total = Спишется {$total} €$, из них комиссия {$fee} €$.
nc-bank-history-title = За этот раунд
nc-bank-history-empty = В этом раунде операций не было.
nc-bank-history-line = {$time}  {$kind}  {$amount} €$
nc-bank-history-fee = (комиссия {$fee})
nc-bank-history-counterparty = ↔ {$counterparty}
nc-bank-history-note = · {$note}

nc-bank-kind-opening = Открытие счёта
nc-bank-kind-deposit = Внесение наличных
nc-bank-kind-withdrawal = Снятие наличных
nc-bank-kind-transfer-out = Перевод отправлен
nc-bank-kind-transfer-in = Перевод получен
nc-bank-kind-salary = Зарплата
nc-bank-kind-admin-grant = Пополнение бюджета
nc-bank-kind-admin-withdrawal = Изъятие из бюджета
nc-bank-kind-salary-missed = Зарплата не выплачена
nc-bank-kind-contract = Оплата контракта
nc-bank-kind-fixer = Оплата фиксера
nc-bank-kind-unknown = Операция

nc-bank-atm-title = Банкомат Банка Найт-Сити
nc-bank-terminal-title = Финансы отдела
nc-bank-terminal-denied = Этим терминалом может пользоваться только глава отдела.
nc-bank-salaries-title = Зарплата за одну выплату
nc-bank-salary-apply = Задать

nc-bank-admin-title = Банк Найт-Сити (админ)
nc-bank-admin-departments = Счета отделов
nc-bank-admin-adjust-title = Пополнить или списать
nc-bank-admin-lookup = Найти
nc-bank-admin-select = Выбрать
nc-bank-admin-amount-placeholder = Сумма
nc-bank-admin-reason-placeholder = Причина (обязательно)
nc-bank-admin-grant = Пополнить
nc-bank-admin-take = Списать
nc-bank-admin-fees-title = Комиссии банка
nc-bank-admin-fee-resident = Жители (личные счета)
nc-bank-admin-owner-character = персонаж
nc-bank-admin-lookup-result = {$number} ({$owner}): {$balance} €$
nc-bank-admin-reason-required = Укажите причину.
nc-bank-admin-adjust-failed = Не выполнено: счёта нет или на нём не хватает денег.
nc-bank-admin-adjusted = {$number}: изменено на {$amount} €$.
nc-bank-admin-fee-set = Ставка комиссии сохранена.

cmd-ncbank-desc = Открывает админ-окно Банка Найт-Сити.
cmd-ncbank-help = Использование: ncbank

ent-NCATM = банкомат
    .desc = Терминал Банка Найт-Сити. Внесение и снятие наличных на свой счёт.
ent-NCBankTerminalBase = финансовый терминал отдела
    .desc = Терминал счёта отдела. Пользоваться им может только глава отдела.
ent-NCBankCartridge = картридж «Банк»
    .desc = Программа Банка Найт-Сити: баланс, история и переводы.

nc-bank-account-militech-hq = Милитех (корпорация)
nc-bank-account-biotechnica-hq = Биотехника (корпорация)
