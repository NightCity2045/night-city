cmd-ncactorlist-description = Показать постоянных сюжетных акторов с полными нейронными профилями.
cmd-ncactorlist-help = Использование: {$command} [фильтр по ключу или имени]
cmd-ncactorspawn-description = Обеспечить физическое присутствие постоянного GPT-актора рядом с игроком.
cmd-ncactorspawn-help = Использование: {$command} <ключ актора> [игрок онлайн]
cmd-ncactor-hint-key = Ключ сюжетного актора
cmd-ncactor-hint-player = Игрок онлайн; если не указан — вы сами

cmd-ncactorlist-count = Найдено сюжетных акторов: {$count}.
cmd-ncactorlist-entry = {$key} | {$name} | профиль {$profile} | Actor {$actor} | {$status}
cmd-ncactor-status-not-imported = не импортирован
cmd-ncactor-status-profile-mismatch = несовпадение постоянного профиля
cmd-ncactor-status-physical = физическая сущность {$entity}
cmd-ncactor-status-abstract = не физический; ожидает восстановления
cmd-ncactor-status-unknown = неизвестно

cmd-ncactor-error-unknown = Неизвестный ключ или имя сюжетного актора: {$key}.
cmd-ncactor-error-ambiguous = Ключ или имя сюжетного актора неоднозначны: {$key}. Используйте полный ключ профиля из ncactorlist.
cmd-ncactor-error-player-offline = Игрок {$player} не в сети.
cmd-ncactor-error-server-needs-player = В серверной консоли необходимо указать игрока онлайн.
cmd-ncactor-error-player-no-entity = Игрок {$player} не управляет физической сущностью.
cmd-ncactor-error-no-city-coordinates = Игрок {$player} не находится в загруженной Z-сети Найт-Сити.
cmd-ncactor-error-not-imported = Актор {$key} ещё не импортирован. Сначала импортируйте настроенный профиль населения.
cmd-ncactor-error-profile-mismatch = Актор {$key} существует, но его постоянный профиль Narrative Actor отсутствует или устарел. Повторите миграционный импорт населения.
cmd-ncactor-error-unknown-reason = диагностическая причина отсутствует
cmd-ncactor-error-materialization = Не удалось обеспечить физическую сущность актора {$key}: {$reason}

cmd-ncactorspawn-requested = Запрос физического жизненного цикла {$request} для актора {$key} ({$name}, {$actor}): {$status}.
cmd-ncactorspawn-completed = Запрос {$request} актора {$key}: {$status} как сущность {$entity}.
cmd-ncactorspawn-failed = Запрос {$request} актора {$key} завершился со статусом «{$status}»: {$reason}
cmd-ncactorspawn-result-unavailable = Результат запроса {$request} актора {$key} больше недоступен для наблюдения.

cmd-ncactor-spawn-status-queued = поставлен в очередь
cmd-ncactor-spawn-status-loading = загружается постоянное состояние
cmd-ncactor-spawn-status-retrying = ожидается безопасный пол
cmd-ncactor-spawn-status-spawned = физический
cmd-ncactor-spawn-status-already = уже физический
cmd-ncactor-spawn-status-rejected = отклонён
