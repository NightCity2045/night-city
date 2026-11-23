nc-meta-error-input = Недопустимый запрос.
nc-meta-error-quota = Достигнут лимит ресурсов.
nc-meta-error-state-migration = Требуется доверенное преобразование состояния. Прежняя программа и её данные сохранены.
nc-meta-error-access = Доступ запрещён.
nc-meta-error-revision = Файл изменился. Загрузите новую версию или объедините изменения.
nc-meta-error-path = Этот путь файла уже занят.
nc-meta-error-request = Этот идентификатор запроса принадлежит другой операции.
nc-meta-error-storage = Не удалось подтвердить запись операции.
nc-meta-error-power = Устройство обесточено.
nc-meta-error-world = Активный мир ещё не готов.
nc-meta-error-device = Устройство недоступно.
nc-meta-saved = Ревизия сохранена.
nc-meta-created = Проект создан.
nc-meta-default-project = Новый проект
nc-meta-ready = Готово.
ent-NCMetaTerminal = терминал разработки META
    .desc = Программируемый компьютер со средой разработки META и средствами восстановления.
ent-NCMetaServer = сервер приложений META
    .desc = Физический сервер для приложений META и сервисов CitiNet.
nc-meta-error-action = Эта операция недоступна.
nc-meta-error-worker = Сервис компиляции недоступен.
nc-meta-compiler-diagnostic = Диагностика компилятора { $code }.

nc-meta-installed = Выпуск надёжно установлен.
nc-meta-started = Программа запущена.
nc-meta-stopped = Программа остановлена.
nc-meta-error-artifact = Проверка целостности или совместимости артефакта не пройдена.

nc-meta-error-lease = Другой редактор удерживает право записи в этот файл.

nc-meta-error-busy = META завершает текущие операции или перегружена. Повторите позже.

nc-meta-test-passed = Изолированный тест VM пройден.
nc-meta-test-failed = Изолированный тест VM не пройден. Изучите трассировку.
nc-meta-test-invalid = Описание теста некорректно.

nc-meta-debug-evaluation-rejected = Для вычисления нужен приостановленный кадр и выражение без побочных эффектов.

ent-NCMetaDoor = программируемый шлюз
    .desc = Шлюз с контроллером META и независимыми штатными блокировками.
ent-NCMetaDoorPanel = дверная панель META
    .desc = Физическая панель запросов со штатным резервным управлением.

nc-meta-claim-device = Привязать устройство META
nc-meta-device-claimed = Владение устройством META сохранено.
nc-meta-device-id = ID устройства META: { $id }
nc-meta-error-link-capacity = У хоста нет свободной ёмкости связей для этих устройств. Сократите кабель или освободите связь.
nc-meta-error-device-taken = Другой сервер уже держит программу, привязанную к одному из этих устройств. Сначала остановите или удалите ту программу.
nc-meta-error-install-host-missing = Устройство не зарегистрировано.
nc-meta-error-install-host-not-managed = Вы не управляете этим хостом или стоите слишком далеко.
nc-meta-error-install-host-not-ready = Хост ещё загружает свою запись. Попробуйте через секунду.
nc-meta-error-install-artifact-blocked = Программа заблокирована проверкой безопасности.
nc-meta-error-install-host-unpowered = У хоста нет питания.
nc-meta-error-install-host-unsupported = Этот хост не запускает пользовательские программы.
nc-meta-error-install-host-cannot-run = Хост сейчас не может запускать программы (неисправность, обслуживание или загрузка).
nc-meta-error-install-abi = Программа собрана под другую среду выполнения, чем у хоста.
nc-meta-error-install-bindings = Подключения не соответствуют тому, что объявляет программа.
nc-meta-error-install-host-interface = Хост не поддерживает тип устройства, который использует программа.
nc-meta-error-install-device-missing = Подключённое устройство не зарегистрировано.
nc-meta-error-install-device-not-ready = Подключённое устройство ещё загружает запись. Попробуйте через секунду.
nc-meta-error-install-device-not-owned = Подключённое устройство не ваше. Откройте его один раз или соедините кабелем, чтобы оно вошло в вашу сеть.
nc-meta-error-install-device-unpowered = У подключённого устройства нет питания.
nc-meta-error-install-device-interface = Подключённое устройство не предоставляет нужный программе интерфейс.
nc-meta-error-install-device-unreachable = Хост не достаёт до подключённого устройства. Проложите между ними кабель данных.
nc-meta-error-install-scopes = Одобренные вами права не покрывают то, что просит программа.
nc-meta-installed-approved = Установлено, права подтверждены. Теперь можно запускать программу.
nc-meta-error-publish-busy = Хранилище занято другим изменением. Попробуйте через секунду.
nc-meta-error-publish-not-registered = Устройство не зарегистрировано.
nc-meta-error-publish-not-managed = Вы не управляете этим сервером или стоите слишком далеко.
nc-meta-error-publish-no-proof = Не удалось подтвердить, что сервер ваш.
nc-meta-error-publish-no-route = У сервера нет маршрута в CitiNet.
nc-meta-error-publish-not-running = Программа не запущена. Сначала запустите её, потом публикуйте.
nc-meta-error-publish-no-view = У программы нет страницы с таким именем. Проверьте имя view в app.json.
nc-meta-error-publish-no-time = Мировые часы сейчас недоступны. Попробуйте через секунду.
nc-meta-error-publish-authority = Владение сервером изменилось во время публикации. Откройте панель заново.
nc-meta-error-publish-storage = Не удалось сохранить публикацию.
nc-meta-error-app-invalid = Программа не запускается: её приложение не принято. {$problem}
