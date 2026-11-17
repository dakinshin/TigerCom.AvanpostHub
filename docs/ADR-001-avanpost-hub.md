# ADR-001. Avanpost Hub: общий локальный хаб для индикаторов

- **Статус:** принято
- **Дата:** 2026-11-17
- **Проект:** tiger.com / AvanpostHub
- **Связано:** OiFlow, Liquidations, AvanpostSync, Rain

## 1. Контекст

Индикаторы Tiger.com получают от терминала OHLC, объём, дельту и `OpenPos`, но не получают
полный рыночный контекст:

- спотовый поток;
- funding и базис;
- OI других бирж;
- ликвидации;
- стакан;
- опционы и медленные макро-признаки.

Сейчас подобные данные добавляются каждым индикатором отдельно. `Liquidations` сам подписывается
на Binance WS. `AvanpostSync` поднимает локальный сервер только тогда, когда его индикатор добавлен
на график. `Rain` ищет `AvanpostSync.SyncHub` через reflection. При появлении новых потребителей
такая схема приводит к дублированию соединений, вычислений и истории.

При этом индикаторы выполняются внутри процесса терминала. Загружать туда сетевые ретраи,
хранение истории, нормализацию контрактов и тяжёлые вычисления нежелательно: это увеличивает
риск подвисания UI и усложняет повторное использование данных.

## 2. Решение

Создать отдельный процесс **Avanpost Hub**.

Это GUI-приложение, работающее в системном трее и способное прописывать себя в автозапуск
пользователя через `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

Avanpost Hub — локальный read-only хаб публичных рыночных данных и overlay-разметки.
Индикаторы являются клиентами хаба и не подключаются к биржам напрямую, кроме периода миграции.

```text
Tiger Terminal
   │
   ├─ OiFlow / Shade ─────────────┐
   ├─ Liquidations ───────────────┤
   ├─ Rain / AvanpostSync ────────┤
   └─ будущие индикаторы ─────────┤
                                  │ localhost WS/HTTP
                                  ▼
                           ┌──────────────┐
Binance Spot ─────────────→│ Avanpost Hub │
Binance USD-M ────────────→│              │
Bybit / OKX / Bitget ─────→│  connectors  │
Deribit / CME ────────────→│  normalizer  │
                           │  features    │
Скринер / scanner ────────→│  overlays    │
                           │  storage     │
                           └──────────────┘
```

## 2.1. Статус реализации v0.2

Реализовано:

- tray-приложение, single-instance, настройки и HKCU-автозапуск;
- localhost HTTP/WebSocket и FeatureStore;
- Binance Spot `aggTrade` и Spot CVD;
- Binance USD-M `aggTrade`, mark/index/funding, basis;
- Binance USD-M OI через REST-опрос;
- Binance USD-M `forceOrder` liquidations;
- raw-поток `liq.events.v1`;
- динамический запуск и остановка коннекторов по подпискам клиентов;
- `Liquidations` переведён на Hub с сохранением прямого Binance fallback.

Ещё не реализовано:

- исторический backfill;
- Bybit/OKX/агрегация OI;
- L2/OFI;
- overlay-протокол AvanpostSync;
- перевод `OiFlow` на Hub.

## 3. Границы

### Входит в Avanpost Hub

- публичные WebSocket/REST-подключения к биржам;
- нормализация инструментов и контрактов;
- расчёт общих признаков: CVD, basis, funding percentile, OI breadth, liquidations;
- хранение live/latest и закрытых баров;
- локальный HTTP/WebSocket API;
- overlay-разметка уровней, зон и маркеров;
- health/status, диагностика, автозапуск.

### Не входит в Avanpost Hub v1

- торговля и выставление ордеров;
- приватные API-ключи;
- счета, позиции и PnL;
- отрисовка на графике;
- знание о состояниях конкретного индикатора (`NewLongs`, `Climax` и т.п.);
- ML/оптимизация порогов.

Торговые и приватные данные должны развиваться отдельно — с другой моделью безопасности.
Hub не должен становиться «бог-объектом», который знает всё о терминале и биржах.

## 4. Архитектура процесса

### 4.1. Host

`AvanpostHub.exe` — WinExe WPF-приложение:

- один экземпляр на пользователя (Mutex `Local\AvanpostHub`);
- при обычном запуске показывает окно настроек;
- при запуске с `--minimized` и/или включённой настройке стартует сразу в трей;
- при закрытии окна сворачивается в трей, а не завершает процесс;
- выход — только через меню трея или явную команду.

### 4.2. Tray

Меню трея:

- «Открыть Avanpost Hub»;
- состояние сервера;
- «Запускать при входе в Windows»;
- «Выход».

### 4.3. Автозапуск

Используется только пользовательский registry hive:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
AvanpostHub = "<путь к AvanpostHub.exe>" --minimized
```

Преимущества:

- не требуются права администратора;
- не нужен Windows Service;
- автозапуск привязан к пользователю, который работает с терминалом.

В v1 используется tray-приложение, а не Windows Service. Причины: проще отладка, меньше
административных требований, нет необходимости работать в Session 0. Если позже понадобится
работа без входа пользователя — это будет отдельное решение.

## 5. Модули

```text
AvanpostHub.Host
  ├─ AvanpostHub.Contracts      DTO и версия протокола
  ├─ AvanpostHub.MarketData     адаптеры бирж
  ├─ AvanpostHub.Features       общие нормализованные признаки
  ├─ AvanpostHub.Overlays       уровни/зоны/маркеры
  ├─ AvanpostHub.Storage        SQLite/Parquet
  ├─ AvanpostHub.Transport      localhost HTTP/WebSocket
  └─ AvanpostHub.Client         тонкий клиент для индикаторов
```

В первой реализации допустимо держать модули в одной сборке, но границы должны сохраняться.

## 6. Модель данных

### 6.1. Канонический инструмент

Каждый инструмент идентифицируется явно:

```text
BINANCE:BTCUSDT:SPOT
BINANCE:BTCUSDT:PERP
BINANCE:BTCUSD:COIN-M
BYBIT:BTCUSDT:PERP
DERIBIT:BTC-27NOV26:OPTION
```

В нормализованной модели должны храниться:

- биржа;
- base/quote;
- market type (`SPOT`, `PERP`, `FUTURE`, `OPTION`);
- settle (`USDT`, `USDC`, coin);
- contract size и единица количества;
- inverse/linear;
- тикер на бирже;
- алиасы и правила маппинга из Tiger.

### 6.2. Feature snapshot

Hub отдаёт не сырые тики, а снимок признаков:

```json
{
  "type": "features",
  "instrument": "BINANCE:BTCUSDT:PERP",
  "spotInstrument": "BINANCE:BTCUSDT:SPOT",
  "timeframe": "1m",
  "barOpenMs": 1763446080000,
  "asOfMs": 1763446138500,
  "final": false,
  "context": {
    "spotCvdZ": 1.4,
    "perpCvdZ": 0.6,
    "spotPerpDiv": 0.8,
    "basisBps": 7.2,
    "fundingPct": 0.91,
    "oiBreadth": 0.8,
    "liqLongUsd": 1200000,
    "liqShortUsd": 300000,
    "liqImbalance": 0.6
  },
  "quality": {
    "stale": false,
    "freshVenues": 4,
    "partialLiquidations": true
  }
}
```

Правила:

- `barOpenMs` — начало бара по UTC;
- `asOfMs` — последнее событие, учтённое в снимке;
- `final` — бар закрыт;
- live-снимок не попадает в историю;
- final-снимок заменяет предыдущий final за тот же `barOpenMs`;
- для истории нельзя использовать события с `event_time > bar_close`.

### 6.3. Feature sets

Наборы признаков версионируются:

- `oi.v1` — OI, funding, basis, breadth;
- `spot.v1` — spot CVD, spot share, spot depth;
- `liq.v1` — long/short liquidations, intensity, imbalance;
- `book.v1` — depth imbalance, OFI;
- `overlay.v1` — уровни, зоны, маркеры;
- `context.v1` — агрегат для потребителей, которым нужен общий контекст.

Изменение формулы или единиц измерения = новый major версии (`oi.v2`).
Добавление необязательного поля допускается в рамках minor-совместимости.

## 7. Транспорт

### 7.1. HTTP

```text
GET /health
GET /v1/status
GET /v1/features/latest?instrument=...&timeframe=1m
GET /v1/features/history?instrument=...&timeframe=1m&fromMs=...&limit=500
```

### 7.2. WebSocket

```text
ws://127.0.0.1:7830/ws
```

Клиент отправляет:

```json
{"type":"subscribe","instrument":"BINANCE:BTCUSDT:PERP","timeframe":"1m"}
```

Hub отвечает:

```json
{"type":"subscribed","instrument":"BINANCE:BTCUSDT:PERP","timeframe":"1m"}
```

и затем отправляет live/final snapshots.

Служебные сообщения:

- `ping` → `pong`;
- `status`;
- `history`;
- `unsubscribe`;
- `error`.

Сервер слушает только `127.0.0.1`. Наружу в интернет ничего не публикуется.

## 8. Хранение

В v1:

- in-memory ring buffer для live/latest;
- история final-баров в SQLite;
- отдельная таблица status/quality;
- логи в `%LOCALAPPDATA%\AvanpostHub\logs`.

Позже для больших объёмов:

- Parquet для сырых агрегатов;
- DuckDB для research/replay;
- отдельное хранение L2-снимков с ограниченным retention.

Нельзя хранить полный L2-стакан за месяцы в SQLite. Для L2 храним агрегаты
(`book_imbalance`, `ofi`, `spread`, `depth`) и только короткую raw-историю.

## 9. Качество данных

Каждый признак должен иметь качество:

- `stale`;
- возраст источника;
- число свежих бирж;
- partial/complete для ликвидаций;
- источник;
- warnings.

Правила:

- stale-биржа исключается из агрегатов;
- при одном свежем venue `oiBreadth` не повышает уверенность;
- Binance `forceOrder` помечается как частичный поток;
- отсутствие данных не заменяется нулём, а означает `null`;
- потребитель обязан ограничивать confidence при плохом quality.

## 10. Миграция

### Этап 0

- Создать `AvanpostHub` и tray-приложение.
- Добавить single-instance, настройки, автозапуск.
- Добавить localhost HTTP/WebSocket и FeatureStore.
- Добавить тесты контрактов и хранилища.

### Этап 1

- Binance Spot + Binance USD-M.
- Spot CVD, basis, funding, OI, liquidations.
- Первый потребитель — `Liquidations`.
- Второй потребитель — `OiFlow` / `OiFlowShade`.

### Этап 2

- Bybit и OKX.
- Aggregate OI, breadth, funding dispersion.
- Кросс-биржевые ликвидации.

### Этап 3

- Spot L2/OFI.
- Усиление absorption-сигналов.
- Deribit/CME/macro slow context.

### Этап 4

- Перенос overlay-протокола из `AvanpostSync`.
- `AvanpostSync` становится клиентом Hub.
- `Rain/AvanpostBridge` перестаёт использовать reflection.
- Старый протокол `setDrawings`/`clearDrawings` сохраняется на переходный период.

## 11. Последствия

### Плюсы

- Одно подключение к бирже на весь терминал.
- Общая история для всех индикаторов.
- Терминал не занимается сетью и нормализацией.
- Индикаторы не зависят от форматов бирж.
- `Liquidations` получает историю и другие биржи.
- `OiFlow` получает спот и кросс-биржевой контекст.
- `Rain` и `AvanpostSync` перестают зависеть от «индикатора-хоста».
- Новые индикаторы подключают готовые feature sets.

### Минусы и риски

- Появляется отдельный процесс, который нужно запускать.
- Нужны автозапуск, health и обработка падений.
- Нужна миграция старых потребителей.
- Кросс-биржевая история может быть недоступна бесплатно.
- Публичные ликвидации неполные; нельзя выдавать их за полный объём.
- Нельзя смешивать публичные рыночные данные и приватную торговлю.

## 12. Критерии приёмки v0.1

- Hub запускается как WinExe и работает в трее.
- Повторный запуск не создаёт второй процесс.
- Есть окно настроек с портом и автозапуском.
- Автозапуск пишется в HKCU и не требует администратора.
- `GET /health` отвечает.
- `GET /v1/status` отдаёт состояние.
- WebSocket `/ws` принимает `ping`, `subscribe`, `status`, `history`.
- FeatureStore хранит только final-бары в истории.
- Тесты ядра проходят без терминала.
- При закрытии окна процесс остаётся в трее; выход — только через меню.

## 13. Открытые вопросы

1. Нужен ли Hub как Windows Service для работы без входа пользователя?
2. Какой вендор использовать для полной истории кросс-биржевых OI и ликвидаций?
3. Нужен ли отдельный `AvanpostTradeHub` для приватных данных?
4. Когда переносить overlay-протокол: до или после первых рыночных коннекторов?
5. Нужна ли аутентификация localhost-соединений (токен/именованный канал) при многопользовательской Windows-сессии?
