# Avanpost Hub

Отдельный GUI-процесс для Tiger.com, который работает в системном трее и раздаёт индикаторам
локальный рыночный контекст: спот, funding, базис, OI других бирж, ликвидации и overlay-разметку.

Hub не должен нагружать процесс терминала сетевыми подключениями, нормализацией контрактов и
хранением истории. Индикаторы подключаются к нему по localhost.

```text
Tiger -> индикатор -> ws://127.0.0.1:7830/ws -> AvanpostHub -> биржи
```

## Текущая версия

v0.2 — Binance Spot + USD-M:

- WPF-приложение с иконкой в трее;
- single-instance;
- окно настроек;
- автозапуск через `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`;
- локальный HTTP-сервер и WebSocket `/ws`;
- `FeatureStore` с live/latest и историей final-баров;
- контракты `oi.v1`, `spot.v1`, `liq.v1`, `context.v1`;
- Binance Spot: `aggTrade`, Spot CVD и spot/perp divergence;
- Binance USD-M: `aggTrade`, mark/index price, funding, basis;
- Binance USD-M: open interest через REST-опрос;
- Binance USD-M: `forceOrder` liquidations;
- raw-поток `liq.events.v1` для индикатора `Liquidations`;
- on-demand запуск коннекторов по подписке индикатора;
- автоматический stop коннекторов при отписке последнего клиента;
- тесты ядра, парсеров и локального транспорта.

### Ограничения v0.2

- Пока только одна биржа: Binance.
- Нет исторического backfill Binance Vision; история начинается с первого запуска Hub.
- Funding percentile и spot/perp CVD z-score появляются после накопления 20 закрытых баров.
- Binance `forceOrder` — неполный поток (не более одного события в секунду на символ).
- Для OI используется USD-M REST `openInterest`, опрос раз в 5 секунд.
- Bybit/OKX, L2-стакан и overlay-протокол ещё не подключены.

## Сборка

Нужны Windows 10/11, .NET 10 SDK и WPF workload.

```powershell
dotnet build .\AvanpostHub\AvanpostHub.csproj -c Release
```

Запускаемый файл после build:

```text
AvanpostHub\bin\Release\net10.0-windows\win-x64\AvanpostHub.exe
```

DLL рядом — это managed-сборка, а не ошибка: EXE является apphost и запускает её.

Для одного самостоятельного EXE без установленного .NET Runtime:

```powershell
dotnet publish .\AvanpostHub\AvanpostHub.csproj -c Release -p:PublishProfile=win-x64
```

Результат:

```text
AvanpostHub\bin\Release\net10.0-windows\win-x64\publish\AvanpostHub.exe
```

Или открыть `AvanpostHub.csproj` в Visual Studio 2022/2026.

## Запуск

```powershell
dotnet run --project .\AvanpostHub\AvanpostHub.csproj
```

Первый запуск показывает окно настроек. Дальше приложение живёт в трее. Закрытие окна
не завершает процесс.

Автозапуск:

- включён в окне настроек;
- пишется в `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`;
- не требует прав администратора;
- запускает приложение с `--minimized`.

## Локальное API

```text
GET http://127.0.0.1:7830/health
GET http://127.0.0.1:7830/v1/status
GET http://127.0.0.1:7830/v1/features/latest?instrument=BINANCE:BTCUSDT:PERP&timeframe=1m
GET http://127.0.0.1:7830/v1/features/history?instrument=BINANCE:BTCUSDT:PERP&timeframe=1m&limit=500
```

WebSocket:

```text
ws://127.0.0.1:7830/ws
```

Пример:

```json
{"type":"subscribe","instrument":"BINANCE:BTCUSDT:PERP","timeframe":"1m"}
```

Служебные команды:

```json
{"type":"ping"}
{"type":"status"}
{"type":"history","instrument":"BINANCE:BTCUSDT:PERP","timeframe":"1m","limit":500}
{"type":"unsubscribe","instrument":"BINANCE:BTCUSDT:PERP","timeframe":"1m"}
```

Сырые ликвидации для `Liquidations`:

```json
{"type":"subscribe","instrument":"BINANCE:BTCUSDT:PERP","feature":"liq.events.v1"}
```

Ответ приходит отдельными сообщениями:

```json
{"type":"liquidation","instrument":"BINANCE:BTCUSDT:PERP","timeMs":1763446080000,"isLong":false,"price":100,"quantity":3,"notional":300,"source":"BINANCE"}
```

## Где лежат файлы

```text
%LOCALAPPDATA%\AvanpostHub\settings.json
%LOCALAPPDATA%\AvanpostHub\logs\avanpost-hub.log
```

## Тесты

```powershell
cd .\AvanpostHub\Tests
dotnet run
```

Тесты проверяют FeatureStore, JSON-контракты, замену final-бара, live/final разделение,
ограничение истории, настройки, Binance JSON-парсеры, feature aggregator и локальный
HTTP/WebSocket-транспорт.

## Архитектурное решение

См. [ADR-001](docs/ADR-001-avanpost-hub.md).

## Как работает подписка

Пока индикатор не подписан, биржевые соединения не открываются.

```json
{"type":"subscribe","instrument":"BINANCE:BTCUSDT:PERP","timeframe":"1m"}
```

Hub запускает для `BTCUSDT`:

- Binance Spot `aggTrade`;
- Binance USD-M `aggTrade`;
- Binance USD-M `markPrice@1s`;
- Binance USD-M `forceOrder`;
- REST-опрос `openInterest` раз в 5 секунд.

После последней отписки соединения останавливаются. Это позволяет держать Hub запущенным,
не расходуя сеть и CPU на символы, которые сейчас никто не смотрит.

## Ближайшие шаги

1. Перевести `OiFlow` на `context.v1` и добавить confidence-слой.
2. Добавить Binance Vision backfill для истории.
3. Добавить Bybit/OKX aggregate OI и funding dispersion.
4. Добавить L2/OFI.
5. Перенести overlay-протокол из `AvanpostSync`.
