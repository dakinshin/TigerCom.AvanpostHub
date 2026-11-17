# Avanpost Hub

Отдельный GUI-процесс для Tiger.com, который работает в системном трее и раздаёт индикаторам
локальный рыночный контекст: спот, funding, базис, OI других бирж, ликвидации и overlay-разметку.

Hub не должен нагружать процесс терминала сетевыми подключениями, нормализацией контрактов и
хранением истории. Индикаторы подключаются к нему по localhost.

```text
Tiger -> индикатор -> ws://127.0.0.1:7830/ws -> AvanpostHub -> биржи
```

## Текущая версия

v0.1 — каркас:

- WPF-приложение с иконкой в трее;
- single-instance;
- окно настроек;
- автозапуск через `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`;
- локальный HTTP-сервер;
- WebSocket `/ws`;
- `FeatureStore` с live/latest и историей final-баров;
- контракты `oi.v1`, `spot.v1`, `liq.v1`, `context.v1`;
- тесты ядра без терминала.

Биржевые коннекторы ещё не включены. Это следующий этап: сначала Binance Spot + USD-M,
затем `Liquidations` и `OiFlow` как первые потребители.

## Сборка

Нужны Windows 10/11, .NET 10 SDK и WPF workload.

```powershell
dotnet build .\AvanpostHub\AvanpostHub.csproj -c Release
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
ограничение истории и санитизацию настроек.

## Архитектурное решение

См. [ADR-001](docs/ADR-001-avanpost-hub.md).

## Ближайшие шаги

1. Добавить Binance Spot: trades/aggTrade, spot CVD, spot share.
2. Добавить Binance USD-M: funding, basis, OI, liquidations.
3. Перевести `Liquidations` на `liq.v1`.
4. Добавить `FlowContext` и confidence в `OiFlow` / Shade.
5. Добавить Bybit/OKX aggregate OI и funding dispersion.
6. Перенести overlay-протокол из `AvanpostSync`.
