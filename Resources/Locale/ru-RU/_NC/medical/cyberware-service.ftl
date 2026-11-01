nc-cyberware-firmware-standard = стандартная управляющая прошивка
nc-cyberware-firmware-service-lock = сервисная блокировка активных режимов
nc-cyberware-service-flashed = Прошивка записана. Перед установкой повторите стендовую проверку.
nc-cyberware-service-invalid = повреждённое сервисное состояние
nc-cyberware-service-mounted = { $item } подключён для локальной прошивки и проверки. Используйте физический носитель или чистый лист бумаги.
nc-cyberware-service-document =
    СЕРВИСНЫЙ ОТЧЁТ О КИБЕРИЗДЕЛИИ
    Технический серийный номер: { $serial }
    Ревизия комплектации: { $revision }; сервисная ревизия: { $service }
    Прошивка: { $firmware }
    Прочность каркаса: { $integrity }; предел ремонта: { $ceiling }
    Нагрев: { $heat }
    { $tested }
    Отчёт описывает изделие на момент проверки. Бумагу и внешнюю маркировку можно изменить; сверяйте технический номер на физическом стенде.
nc-cyberware-service-admin-description = Просмотр внутренних ID киберизделий, прошивки и истории обслуживания.
nc-cyberware-service-admin-help = nccyberwarestatus <сетевой uid тела или отдельного модуля>
nc-cyberware-service-admin-empty = Физические киберизделия не найдены.
nc-cyberware-service-admin-humanity = H { $current } | B { $burden } | S { $stress } | индекс стадии кризиса { $stage } | пакетов адаптации { $adaptations } | недавних воздействий { $impulses }
nc-cyberware-service-admin-item = { $entity } | ID { $serial } | сервис { $revision } | { $firmware } | I { $integrity } | D { $defect } | нагрев { $heat } | архивировано событий { $omitted }
nc-cyberware-service-admin-entry = ревизия { $revision } | { $operation } | владелец { $owner } | ResolutionId { $resolution }
nc-cyberware-service-operation-0 = установлено
nc-cyberware-service-operation-1 = извлечено
nc-cyberware-service-operation-2 = изменена комплектация
nc-cyberware-service-operation-3 = отремонтировано
nc-cyberware-service-operation-4 = проверено на стенде
nc-cyberware-service-operation-5 = записана прошивка
ent-NCCyberwareStandardFirmwareCard = карта стандартной прошивки киберизделий
    .desc = Многоразовый физический носитель для восстановления стандартной прошивки на сервисном стенде.
ent-NCCyberwareServiceLockCard = карта сервисной блокировки киберизделий
    .desc = Многоразовый физический носитель, запрещающий активные способности киберизделия. Пассивное жизнеобеспечение сохраняется.
