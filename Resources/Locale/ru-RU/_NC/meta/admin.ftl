# SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
nc-meta-admin-description = Проверить и остановить META без чтения закрытых исходников.
nc-meta-admin-help = ncmeta status/repro [hostNetEntity]; enabled true/false; stop process/device/principal <id>; block/unblock/quarantine <hash>; revoke <installation-id> <причина>
nc-meta-admin-summary = META включена: {$enabled}; политика корректна: {$valid}; процессов: {$processes}; хостов: {$hosts}; команд в очереди: {$pending}; заблокированных hash: {$blocked}.
nc-meta-admin-stopped = Остановлено процессов META: {$count}. Ручное управление доменными устройствами доступно.
nc-meta-admin-pending = Отзыв полномочий ожидает подтверждения записи в БД.
nc-meta-admin-revoked = Полномочия установки отозваны с подтверждением записи в БД.
nc-meta-admin-policy-saved = Политика применена, сохранение конфигурации запрошено. Перед перезапуском проверьте журнал сервера.
nc-meta-admin-policy-unsaved = Политика применена, но конфигурация сервера не сохранена. Сохраните её перед перезапуском.
