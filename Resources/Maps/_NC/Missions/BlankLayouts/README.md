# Пустые карты миссий City Zones

Выгрузка всех шести MissionSlot из `Resources/Prototypes/_NC/City/city-zones.yml`.
На картах только бетонный пол по точному контуру зоны: без стен, мебели, NPC и маркеров.
Координаты нормализованы; исходный контур, GUID, этаж и смещение в город сохранены в `mission-slots.json`.

| Зона | Z | Размер | Тайлов | Карта |
|---|---|---|---|---|
| G101 Mission T2 | 1 | 5 × 10 | 46 | [G101MissionT2Z1.yml](G101MissionT2Z1.yml) |
| RZ30 01 Mission T3 | 3 | 9 × 8 | 59 | [RZ3001MissionT3Z3.yml](RZ3001MissionT3Z3.yml) |
| G103 Mission T3 | 0 | 13 × 11 | 118 | [G103MissionT3Z0.yml](G103MissionT3Z0.yml) |
| G103 Mission T3 | 1 | 13 × 11 | 118 | [G103MissionT3Z1.yml](G103MissionT3Z1.yml) |
| G103 Mission T3 | 2 | 13 × 11 | 118 | [G103MissionT3Z2.yml](G103MissionT3Z2.yml) |
| RZ30 03 Mission T4 | 2 | 15 × 18 | 139 | [RZ3003MissionT4Z2.yml](RZ3003MissionT4Z2.yml) |
| Mission: The Slammer | 0 | 30 × 16 | 480 | [TheSlammerZ0.yml](TheSlammerZ0.yml) |
| RZ 02 Mission T2 | 1 | 6 × 9 | 46 | [RZ02MissionT2Z1.yml](RZ02MissionT2Z1.yml) |

G103: подготовлены все три существующих уровня здания — Z=0, Z=1 и Z=2 (relativeZ=0,1,2). Наличие полов проверено по сохранённым NightCity-картам; на Z=-4…-1 и Z=3…4 в здании нет непустых тайлов. На каждом этаже сохранён полный контур миссии — 118 тайлов. `G103MissionT3Global.yml` сохранён как исходная заготовка контура.

В City Zones у G103 пока остаётся Global Z. Перед подключением ncZoneTemplate нужно заменить его явным диапазоном Z=0…2; исходная разметка города этой подготовкой не изменялась.

Заготовки пока не зарегистрированы как игровые миссии: после маппинга добавьте маркеры и ncZoneTemplate/ncMissionDefinition. При нескольких этажах укажите relativeZ относительно нижнего этажа.
