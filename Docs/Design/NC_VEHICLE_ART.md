# Toyota Supra 1996 — исходный спрайт

## Детали и оборудование автомастерской — 2026-09-08

Новые оригинальные изображения созданы встроенным OpenAI ImageGen по запросу пользователя. Сторонние изображения в генерацию не передавались. Экспорт: `Resources/Textures/_NC/Interface/VehicleWorkshop/` — 60 прозрачных UI PNG по 128×128; `parts.rsi` — 48 состояний 32×32; `tools.rsi` — 8 состояний 32×32; `equipment.rsi` — 4 состояния 64×64. У предметов и оборудования одно направление, стандартный `meta.json`, реальные прозрачные пиксели. Атрибуция экспорта — Astro, CC-BY-4.0, как указано в RSI.

Генерация выполнялась листами из отдельных изношенных автомобильных деталей на прозрачном фоне, без надписей и интерфейса. Стиль: читаемый контур, серый металл, красные кузовные детали, вид сверху или технический ракурс для соответствующего узла. Пользователь ранее разрешил обработку скриптом: листы разделены на ячейки, удалены отдельные фрагменты соседних ячеек, сохранена исходная альфа, изображения уменьшены и помещены в квадратные кадры. Полноразмерные листы не входят в игровые ресурсы.

Исходники хранятся в каталоге генератора текущей задачи; имена файлов и порядок ячеек:

- `exec-d16a01b6-ace6-45de-875b-cfa6afd7601f.png`, 4×4: колесо, диск, суппорт, ступица; колодки, рычаг, стойка, рулевой наконечник; двигатель, коробка, аккумулятор, радиатор; шланг, разъём, гайка, крыло.
- `exec-f438bf66-d425-4e00-a771-69ef2ad30dc9.png`, 4×4: стартер, генератор, ЭБУ, катушка; топливный насос, фильтр, водяной насос, вентилятор; термостат, масляный насос, фильтр, рулевая рейка; главный тормозной цилиндр, трубки, сцепление, кардан.
- `exec-ae6967ba-8ae1-42f3-94c5-ec724f180fa5.png`, 4×4: дифференциал, капот, передний и задний бамперы; фары, сигнал, насос омывателя, мотор дворников; подушка безопасности, насос усилителя, бак, раздатка; отдельные обод, шина, пружина и амортизатор.
- `exec-8f7e0556-0a01-4859-bed3-dde2cc2b864e.png`, 4×3: подъёмник, кран, верстак, шиномонтажный станок; набор головок, мультиметр, сканер, сливной поддон; динамометрический ключ, зарядное устройство, прибор развала, сварочное оборудование.

Маски выбора 32×32 экспортируются из альфа-канала UI-спрайтов с учётом отдельных рабочих точек и сохраняются в `workshop-views.yml`. Сервер не загружает текстуры; клиент и сервер используют одну геометрию. Это маски интерфейса, а не физические коллайдеры машины.

## Эвакуатор

Спрайт `Resources/Textures/_NC/Vehicle/towtruck.rsi/body.png` создан OpenAI ImageGen для этого проекта: строгий вид сверху, кабина вперёд, пустая платформа с лебёдкой и рампами, изношенная техника Night City. Источник — RGBA-кадр 1024×1536 `exec-d029a21b-9fb1-4305-a27b-8cd3078d4dee.png`. Игровой экспорт от 2026-09-08: **256×256 на направление**, лист 512×512 с четырьмя направлениями, видимый силуэт 82×252 пикселя (252×82 в горизонтальных кадрах), масштаб Sprite **1**, коллайдер 2.2×7.9 единицы мира.

Пользователь разрешил локальную обработку скриптом. При подготовке игрового экспорта удалены полупрозрачный ореол и лишние поля, альфа приведена к 0/255, силуэт уменьшен nearest-neighbor и помещён по центру кадра. Чёрный фон предпросмотра сам по себе не означает отсутствие альфа-канала.

## Supra

Создан встроенным OpenAI ImageGen 2026-09-06 по запросу пользователя. Игровой экспорт от 2026-09-08 в `Resources/Textures/_NC/Vehicle/supra_1996.rsi/body.png`: **128×128 на направление**, лист 256×256 с четырьмя направлениями, видимый силуэт 49×112 пикселей (112×49 в горизонтальных кадрах), масштаб Sprite **1**.

Исходник 1024×1536 уменьшен после очистки альфа-канала и обрезки полей тем же способом, что и эвакуатор. Коллайдер 1.45×3.5 единицы мира не менялся. У каждой машины обычная структура RSI: папка `.rsi`, `meta.json` с фактическим размером кадра и состоянием `body` (`directions: 4`), файл `body.png` в RGBA. Крупные исходники в игровые RSI не входят.

Потребитель: `Resources/Prototypes/_NC/Vehicle/vehicles.yml`, прототип `NCToyotaSupra1996`, состояние `body`. Штатный `RobustToolbox/Schemas/validate_rsis.py Resources/Textures/_NC/Vehicle` прошёл без ошибок. Внешние поля изображения имеют alpha=0; RGB под прозрачными пикселями не является нарисованным фоном. Игровой рендер с вращением ещё не проверен.

Использованный prompt:

> Create a production game sprite of a 1996 Toyota Supra A80 / Mk4 for a Space Station 14 top-down pixel-art game. Single car ONLY, strict orthographic directly overhead view, front bumper pointing to the TOP, rear bumper to the BOTTOM. Recognizable factory A80 long rounded hood, curved dark windshield, compact cabin, rounded rear, iconic raised rear wing visible across rear deck, red paint slightly worn with subtle grime for Night City 2045, black tires, small mirrors, small pale headlights and red rear lights. True pixel art with crisp square pixels and restrained 16-32 color palette, dark one-pixel outline; no anti-aliased painterly or photorealistic details. No isometric perspective, no side view, no scene, no ground, no cast shadow, no text, no watermark. Genuine transparent RGBA background. Center the whole car on a portrait canvas, leave transparent margins all around, nothing clipped. Intended in-game footprint about 56 pixels wide by 140 pixels long on a 96 by 160 pixel transparent canvas. Output the clean sprite suitable for nearest-neighbor reduction to that canvas, not a sprite sheet or multiple views. Preserve recognizable 1996 stock Supra body and wing, not a modern 2020 Supra.


Четыре направления упакованы в лист 2×2: South, North, East, West, слева направо и сверху вниз. При строгом виде сверху использованы точные повороты исходного пиксельного силуэта без интерполяции. Квадратный размер кадра сохраняет горизонтальные виды целиком. Sprite.rotation = 180 согласует штатный южный нулевой угол RSI с передом шасси по +Y; noRot остаётся false для плавного вращения на промежуточных курсах. Эвакуатор наследует эту настройку. Движок не изменялся.
