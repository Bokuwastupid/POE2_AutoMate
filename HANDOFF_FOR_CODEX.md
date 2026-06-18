# POE2 AutoMate — Полный Handoff (для Codex / следующего агента)

Дата: 2026-06-18. Самодостаточная точка передачи проекта. Цель: AFK map-runner бот для Path of Exile 2,
работающий через **чтение памяти** живого клиента + симуляцию ввода. Подробная история итераций — в
`UPDATE_LOG.md`; краткий план — в `NEXT_STEPS.md`. Этот файл объединяет всё.

---

## 0. TL;DR — текущее состояние
Полный путь **hideout → подготовка waystone → выбор карты в атласе → активация → вход в портал → клир/босс →
выход** в основном работает, по кускам оттестирован живьём. Слабые места сейчас: **панинг атласа**,
**фильтры агра по сущностям**, **застревание в стенах**. Последний билд собирается `0 warnings / 0 errors`.

Главный принцип: **нельзя «сделать действие через память»** — серверные транзакции (вставить waystone,
выбрать ноду, взять из стэша, активировать карту, войти) идут через сеть, сервер авторитет. Память даёт
только ЧТЕНИЕ состояния; действия — только реальный клик/нажатие (`SendInput`). Кики были от частоты
действий (серверный rate-limit), а не от детекта синтетического ввода → решено глобальным троттлом.

---

## 1. Архитектура
Solution `POE2_AutoMate.sln`, .NET 10 WPF (Windows). Проекты:
- **`src/Core`** (`POE2_AutoMate.Core`) — всё чтение памяти и игровые структуры.
  - `Memory/` — `ProcessReader` (ReadMemory/ReadStructure/ReadPointer/ReadString), `AobScanner`, сигнатуры.
  - `Game/GameReader.cs` — главный фасад: коннект, AOB-скан, `ReadSnapshot()` (player, entities, terrain,
    UI, monster counter, inventory, landmarks), категоризация сущностей, atlas/landmark/ui API.
  - `Game/Poe2Offsets.cs` — все оффсеты.
  - `Game/Poe2UiGeometry.cs` — проекция UiElement в экранные пиксели + поиск кнопки по тексту + детект грида.
  - `Game/Poe2AtlasNodes.cs` — чтение нод атласа + граф связей + маркер «вы здесь».
  - `Game/GameStructs.cs`, `InventorySnapshot.cs`, `MapProjection.cs`, `CustomLandmarkData.cs`.
- **`src/Automation`** (`POE2_AutoMate.Automation`) — логика бота.
  - `AutoMapper.cs` — стейт-машина hideout→map (DetectLocation, PrepareWaystone, FindMapDevice, OpenAtlas,
    SelectMap, ActivateMap, EnterPortal, WaitForMap, ClearMap, ReturnPortal, DumpInventory, NextRun).
  - `CombatBot.cs` — бой/движение/лут/таргетинг внутри карты.
  - `RoutePlanner.cs` — A* по walkable-terrain гриду.
  - `GameInputController.cs` — `SendInput` (клавиши/мышь) + глобальный троттл действий.
  - `MapRunner.cs`, `LootValueTracker.cs`.
- **`src/POE2_AutoMate`** (WPF UI) — `MainWindow.xaml(.cs)` (экраны + live loop 250мс + провайдеры),
  `App.xaml` (тема/стили), overlay-окна.
- **`tools/POE2_SignatureFinder`** — отдельная утилита поиска сигнатур.
- Reference (read-only, откуда портируем логику): `A:\projects\POE2Radar_ref\POE2Radar-0.13.1`.

### Как живёт live loop (MainWindow)
Фоновый таск читает снапшот и гоняет AutoMapper+CombatBot. Есть адаптивный темп: в бою (`ClearMap`)
лёгкий снапшот (`ReadCombatSnapshot`, без UI/inventory) + быстрый тик; вне боя — полный снапшот + 250мс.
Провайдеры, прокинутые в AutoMapper: `AtlasNodeProvider` (ноды+граф+current+размер окна), `UiButtonLocator`
(найти Traverse в полном дереве и спроецировать).

---

## 2. Сборка / запуск
- `dotnet` НЕ в PATH: `& "C:\Program Files\dotnet\dotnet.exe" build "A:\projects\POE2_AutoMate\POE2_AutoMate.sln" -c Release`
- Exe: `src\POE2_AutoMate\bin\Release\net10.0-windows\POE2_AutoMate.exe`.
- MSB3027 «файл занят» = exe запущен (часто от админа; `Stop-Process` может дать Access denied). Закрыть
  POE2_AutoMate вручную и пересобрать. Компиляция при этом проходит — падает только КОПИРОВАНИЕ dll.
- Тест только на живой игре `PathOfExileSteam`. Агент сам игру не запускает/не смотрит — пользователь гоняет
  и шлёт `logs\latest.log`. Конвенция: после изменений — дописать в `UPDATE_LOG.md`, билд 0/0.
- Среда пользователя: **1920×1080 borderless**. Важно: **атлас НЕ центрируется** — открывается на последней
  позиции пана.

---

## 3. Ключевые знания по памяти (решённые цепочки)
Все оффсеты — в `Poe2Offsets.cs`. Главное:
- **Inventory chain** (работает): AreaInstance `+0x580`→ServerData; ServerData `+0x48`→StdVector
  PlayerServerData[0]→ServerDataStructure; `+0x320`→PlayerInventories (stride 0x18, id=1 MainInventory);
  Inventory `+0x150` TotalBoxes, `+0x170` ItemList; InventoryItem `+0x00` itemEntity, slots `+0x08`/`+0x10`.
  ВАЖНО: `SlotEnd` ЭКСКЛЮЗИВНЫЙ (start+size) — берём `End-1`.
- **UI projection** (`Poe2UiGeometry`): экранная позиция = parent-chain unscaled pos × scale; v1=winW/2560,
  v2=winH/1600; ScaleIndex `+0x18A`, LocalScaleMul `+0x130`, RelativePos `+0x118`, Flags `+0x180`
  (visible bit 0x0B), Size `+0x288/+0x28C`, Text `+0x390`, Children `+0x10/+0x18`, Parent `+0xB8`.
- **Atlas nodes** (`Poe2AtlasNodes`): ноды — UiElement отдельного класса (детект BFS по biome-spread+размер
  ~40px). MapNodeId `+0x300`, Content `+0x310`, GridPos `+0x320`, State `+0x32C`, Biome `+0x32E`,
  Flags `+0x32F` (bit0 unlocked, bit1 visited). Имя карты — через `+0x300` → WorldAreas row `+0x08`.
  Проекция ноды в экран — СВОЯ формула: `relPos × winH/1600 × medianZoom` (НЕ parent-chain).
  Граф связей: canvas `+0x5A8` StdVector рёбер (stride 20, source `+0x04`, target `+0x0C` — grid coords).
  Маркер current: единственный НЕ-нода элемент, чей `+0x300` указывает на ноду.
- **Entity life**: Life-компонент, Health vital `+0x1B0`, EnergyShield `+0x248` (Max `+0x2C`, Current `+0x30`).
  `EntityData` теперь несёт `EnergyShield`/`MaxEnergyShield`; `EffectiveHealth = Health + ES`.
- **Landmarks/арена**: тайлы террейна (`TgtPath` .tdt). Boss-арена детектится обобщённо: путь содержит
  `Arena` или `Boss` (`GameReader.IsBossArenaTilePath`) — без курации под карту.

---

## 4. Что СДЕЛАНО (работает / реализовано)
**Инфраструктура:** автоаттач к `PathOfExileSteam`, AOB-скан, логи в проекте, overlay/HUD, радар с walkable.

**Чтение из памяти:** player, entities (+life+ES), terrain (walkable), UI-дерево, monster counter, inventory
(memory-decoded), landmarks, atlas nodes+граф, current-нода.

**Inventory:** memory-сканер, авто-калибровка грида из памяти (кнопка «Calibrate grid from memory»),
protected safe slots 12×5, фикс эксклюзивного SlotEnd.

**Waystone:** hover→Ctrl+C→парс tier/rarity/revives; Orb of Alchemy на normal; обычный Exalted (не Greater/
Perfect); skip плохих; max tier; min/exact revives. Self-heal: открыть инвентарь, если Ctrl+C пуст.

**AutoStart стейт-машина:** hideout-детект, skip подготовки если уже на карте, F2 = запуск AutoStart loop,
CombatBot стартует только на стадии ClearMap. Подготовка waystone ДО устройства карт (когда рюкзак виден).
Стэш-заглушка отключена (`TakeSuppliesFromStash=false`) — припасы кладутся в рюкзак руками.

**Atlas/выбор карты (memory-driven):** ноды читаются из памяти (имя/код/state/flags/content/grid/screen);
выбор только среди **связанных ребром** с текущей нодой; фильтры: `Map*` (не hideout/tower/citadel/boss/
unique/merchant), unlocked && !visited && visible && on-screen, не в блэклисте, без decorated tags. Boss rush
маршрутизирует к boss-interest ноде. reference = маркер → последняя пройденная нода. Авто-дамп атласа на 1м круге.

**Map device / portal:** клик устройства, открытие атласа, выбор ноды кликом по проекции, поиск Traverse в
ПОЛНОМ UI-дереве из памяти + клик по экранным координатам. EnterPortal ждёт появления портала, начальный
settle 5с до клика, ретрай 7с, ждёт реальной смены area, `WaitForMap` settle ~5с до ClearMap.

**Combat:** автоатака по биндам, combo для rare/unique/boss, kite (+питьё HP-фласки при кайте), лут (только
если рядом нет активных мобов), приоритет целей (ближайшая ценная: Rare/Unique/Boss/Encounter; unique ближе
rare → unique), LoS через terrain, route-движение (A*), reachable-only explore (не таранит стену в explore),
directed boss-seek (вглубь от точки входа), детект арены по тайлам.

**Боссы:** `IsMapBoss` (требует «Boss»/«MapBoss» metadata — Exile/unique не считается боссом для завершения).
Подтверждение убийства только «виделся живым (Health>0) → затем мёртв» (нет ложных при интро/ES). Boss rush:
босс убит → выход из карты. dormant/event-locked мобы (нет живого HP) игнорятся, пока не активируются.

**ES-фикс:** прогресс по цели = падение эффективного HP (жизнь+ES) → ES-моб/босс не считается «неуязвимым».

**Анти-кик:** глобальный троттл действий (`GameInputController.GateAction`, ~90мс + джиттер, общий на процесс).

**UI:** новая тема `App.xaml` (палитра, кнопки, поля, чекбоксы, комбо, табы, скроллбары). Экраны: Dashboard/
AutoStart/Inventory/Combat/Loot/Atlas/Debug/Radar/Dev.

---

## 5. TODO / открытые баги (приоритет сверху)
1. **Atlas не умеет панить** → выбирает только из нод, видимых на экране; связанная нода может быть за кадром
   → не кликается. НУЖНО: панить/скроллить атлас к текущей ноде (drag мышью / хоткей центрирования), чтобы
   current+соседи попадали на экран. Главный блокер надёжного выбора карты. (`AutoMapper.SelectMap`,
   `MainWindow.ReadAtlasSelection`.)
2. **Фильтры агра по сущностям** (`GameReader.Categorize`/`IsNonCombat`, `CombatBot.ShouldIgnoreTargetMetadata`/
   `IsCombatMonster`):
   - **НЕ** агриться на **Azmiri spirit**.
   - **ДОЛЖЕН** агриться на мобов из **зеркала Delirium** (валидные цели — не зарезать их фильтром).
   - **НЕ** агриться на «ветерки»/проджектайлы/ground-эффекты от мобов.
   Нужен лог metadata этих сущностей с живой игры, чтобы точно настроить (не задев Delirium-мобов).
3. **Pathfinding: упирается в стену с зазором и не перепланирует** (приходилось помогать вручную). НУЖНО:
   детект «застрял» (позиция почти не меняется N сек при активном move) → форс переплан / другая frontier /
   отойти-обойти. (`CombatBot.TryMoveAlongRoute`/`TryExploreMove`/`PickGlobalExploreWaypoint`, `RoutePlanner`.)
4. **Проверить на свежем билде:** portal wait (5с+7с) реально успевает прогрузку; полный boss-rush цикл
   (связанная карта → вход → арена → ES-босс бьётся → выход после реального килла; Exile по пути не завершает).
5. **StashService** (отложено): готового декодера стэша в reference НЕТ. Делать через UI-проекцию открытой
   вкладки (ячейки = UiElement → item-entity → Ctrl+ЛКМ перенос) + детект/переключение вкладок по имени из
   настроек (`WaystoneStashTabName` и т.д.), затем включить `TakeSuppliesFromStash`.
6. **UI layout-полировка** (тема готова): скруглить карточки в `MainWindow.xaml`, выровнять отступы,
   сгруппировать настройки, статус-бейджи. Можно использовать готовые стили `Card`/`SectionHeader`/`AccentButton`.
7. **Прочее из бэклога:** BossRush HUD-лейбл (vs ClearMap), death/revive (нажать revive при смерти), atlas
   graph A* (граф уже строится — можно полноценный маршрут, а не пошаговый сосед), шрайны/баффы в boss rush.

---

## 6. Карта ключевых файлов
| Файл | Назначение |
|---|---|
| `src/Core/Game/GameReader.cs` | чтение памяти, снапшот, `Categorize`, инвентарь, landmarks, atlas/ui API, ES |
| `src/Core/Game/Poe2Offsets.cs` | все оффсеты |
| `src/Core/Game/Poe2UiGeometry.cs` | проекция UI→экран, поиск кнопок, детект грида |
| `src/Core/Game/Poe2AtlasNodes.cs` | ноды атласа, граф, маркер current |
| `src/Automation/AutoMapper.cs` | стейт-машина, выбор карты, portal/boss-логика |
| `src/Automation/CombatBot.cs` | бой/движение/лут/таргетинг/explore/boss-state/фильтры |
| `src/Automation/RoutePlanner.cs` | A* по terrain |
| `src/Automation/GameInputController.cs` | SendInput + троттл |
| `src/POE2_AutoMate/MainWindow.xaml(.cs)` | UI layout + live loop + провайдеры |
| `src/POE2_AutoMate/App.xaml` | тема/стили |

## 7. Принципы / грабли
- Только чтение из памяти + реальный ввод.
- Держать частоту действий человеческой (троттл) — иначе серверный дисконнект.
- Атлас не центрируется; current-ноду брать из маркера (или последней пройденной), не из центра экрана.
- Мобы без читаемого живого HP — dormant/event-locked, не таргетить, пока не оживут.
- ES: прогресс/«неуязвимость» считать по эффективному HP (жизнь+ES), не по жизни.
- Map boss для завершения ≠ любой unique (Exile). Подтверждать килл только «жив→мёртв».
- После правок: билд 0/0, дописать `UPDATE_LOG.md`.
