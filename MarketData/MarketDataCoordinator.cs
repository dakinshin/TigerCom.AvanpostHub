using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AvanpostHub.MarketData
{
    /// <summary>
    /// Запускает Binance-потоки только при появлении подписки от индикатора
    /// и останавливает их, когда последний клиент отписался.
    /// </summary>
    public sealed class MarketDataCoordinator : IDisposable
    {
        private readonly FeatureStore _store;
        private readonly object _gate = new object();
        private readonly Dictionary<string, SeriesEntry> _series =
            new Dictionary<string, SeriesEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SymbolEntry> _symbols =
            new Dictionary<string, SymbolEntry>(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;

        public MarketDataCoordinator(FeatureStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public event Action<string, BinanceLiquidation> LiquidationReceived;

        public int ConnectorCount
        {
            get
            {
                lock (_gate)
                    return _symbols.Values.Sum(x => (x.Spot != null ? 1 : 0) + (x.Futures != null ? 1 : 0));
            }
        }

        public void Activate(string instrument, string timeframe)
        {
            BinanceInstrument parsed;
            if (!BinanceInstrument.TryParse(instrument, out parsed)) return;

            var seriesKey = FeatureStore.Key(parsed.Instrument, timeframe);
            var toStart = new List<IMarketDataConnector>();

            lock (_gate)
            {
                if (_disposed || _series.ContainsKey(seriesKey)) return;

                SymbolEntry symbol;
                if (!_symbols.TryGetValue(parsed.Symbol, out symbol))
                {
                    symbol = new SymbolEntry(parsed.Symbol);
                    _symbols[parsed.Symbol] = symbol;
                }

                EnsureSpot(symbol, toStart);
                if (parsed.Market == BinanceMarketType.Perpetual)
                    EnsureFutures(symbol, toStart);

                var aggregator = new BinanceFeatureAggregator(
                    _store,
                    parsed.Instrument,
                    timeframe,
                    parsed.Market == BinanceMarketType.Spot);
                symbol.Aggregators.Add(aggregator);
                _series[seriesKey] = new SeriesEntry(parsed.Instrument, timeframe, symbol, aggregator);
                HubLog.Info("market subscribe " + parsed.Instrument + " " + timeframe);
            }

            foreach (var connector in toStart)
                _ = StartConnectorAsync(connector);
        }

        public void ActivateLiquidations(string instrument)
        {
            BinanceInstrument parsed;
            if (!BinanceInstrument.TryParse(instrument, out parsed) ||
                parsed.Market != BinanceMarketType.Perpetual) return;

            var toStart = new List<IMarketDataConnector>();
            lock (_gate)
            {
                if (_disposed) return;
                SymbolEntry symbol;
                if (!_symbols.TryGetValue(parsed.Symbol, out symbol))
                {
                    symbol = new SymbolEntry(parsed.Symbol);
                    _symbols[parsed.Symbol] = symbol;
                }
                symbol.RawLiquidationActive = true;
                EnsureFutures(symbol, toStart);
                HubLog.Info("liquidation subscribe " + parsed.Instrument);
            }

            foreach (var connector in toStart) _ = StartConnectorAsync(connector);
        }

        public void DeactivateLiquidations(string instrument)
        {
            BinanceInstrument parsed;
            if (!BinanceInstrument.TryParse(instrument, out parsed) ||
                parsed.Market != BinanceMarketType.Perpetual) return;

            var toStop = new List<IMarketDataConnector>();
            lock (_gate)
            {
                SymbolEntry symbol;
                if (!_symbols.TryGetValue(parsed.Symbol, out symbol)) return;
                symbol.RawLiquidationActive = false;
                if (symbol.Aggregators.Count == 0 && !symbol.RawLiquidationActive)
                {
                    _symbols.Remove(parsed.Symbol);
                    if (symbol.Spot != null) toStop.Add(symbol.Spot);
                    if (symbol.Futures != null) toStop.Add(symbol.Futures);
                }
                HubLog.Info("liquidation unsubscribe " + parsed.Instrument);
            }

            foreach (var connector in toStop) _ = StopConnectorAsync(connector);
        }

        public void Deactivate(string instrument, string timeframe)
        {
            BinanceInstrument parsed;
            if (!BinanceInstrument.TryParse(instrument, out parsed)) return;

            var seriesKey = FeatureStore.Key(parsed.Instrument, timeframe);
            var toStop = new List<IMarketDataConnector>();

            lock (_gate)
            {
                SeriesEntry series;
                if (!_series.TryGetValue(seriesKey, out series)) return;
                _series.Remove(seriesKey);
                series.Symbol.Aggregators.Remove(series.Aggregator);

                if (series.Symbol.Aggregators.Count == 0 && !series.Symbol.RawLiquidationActive)
                {
                    _symbols.Remove(series.Symbol.Symbol);
                    if (series.Symbol.Spot != null) toStop.Add(series.Symbol.Spot);
                    if (series.Symbol.Futures != null) toStop.Add(series.Symbol.Futures);
                }

                HubLog.Info("market unsubscribe " + parsed.Instrument + " " + timeframe);
            }

            foreach (var connector in toStop)
                _ = StopConnectorAsync(connector);
        }

        public void Dispose()
        {
            List<IMarketDataConnector> connectors;
            lock (_gate)
            {
                _disposed = true;
                _series.Clear();
                connectors = _symbols.Values
                    .SelectMany(x => new IMarketDataConnector[] { x.Spot, x.Futures })
                    .Where(x => x != null)
                    .ToList();
                _symbols.Clear();
            }

            foreach (var connector in connectors)
            {
                try { connector.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); }
                catch { }
                try { connector.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                catch { }
            }
        }

        private void EnsureSpot(SymbolEntry symbol, List<IMarketDataConnector> toStart)
        {
            if (symbol.Spot != null) return;
            var connector = new BinanceSpotConnector(symbol.Symbol, HubLog.Warn);
            connector.Trade += trade => OnSpotTrade(symbol.Symbol, trade);
            symbol.Spot = connector;
            toStart.Add(connector);
        }

        private void EnsureFutures(SymbolEntry symbol, List<IMarketDataConnector> toStart)
        {
            if (symbol.Futures != null) return;
            var connector = new BinanceFuturesConnector(symbol.Symbol, HubLog.Warn);
            connector.Trade += trade => OnPerpTrade(symbol.Symbol, trade);
            connector.MarkPrice += mark => OnMarkPrice(symbol.Symbol, mark);
            connector.OpenInterest += oi => OnOpenInterest(symbol.Symbol, oi);
            connector.Liquidation += liq => OnLiquidation(symbol.Symbol, liq);
            symbol.Futures = connector;
            toStart.Add(connector);
        }

        private void OnSpotTrade(string symbolKey, BinanceTrade trade)
        {
            var aggregators = GetAggregators(symbolKey);
            foreach (var aggregator in aggregators) aggregator.OnSpotTrade(trade);
        }

        private void OnPerpTrade(string symbolKey, BinanceTrade trade)
        {
            var aggregators = GetAggregators(symbolKey);
            foreach (var aggregator in aggregators) aggregator.OnPerpTrade(trade);
        }

        private void OnMarkPrice(string symbolKey, BinanceMarkPrice mark)
        {
            var aggregators = GetAggregators(symbolKey);
            foreach (var aggregator in aggregators) aggregator.OnMarkPrice(mark);
        }

        private void OnOpenInterest(string symbolKey, BinanceOpenInterest oi)
        {
            var aggregators = GetAggregators(symbolKey);
            foreach (var aggregator in aggregators) aggregator.OnOpenInterest(oi);
        }

        private void OnLiquidation(string symbolKey, BinanceLiquidation liquidation)
        {
            var aggregators = GetAggregators(symbolKey);
            foreach (var aggregator in aggregators) aggregator.OnLiquidation(liquidation);

            var handler = LiquidationReceived;
            if (handler == null) return;
            try { handler("BINANCE:" + symbolKey + ":PERP", liquidation); }
            catch (Exception ex) { HubLog.Warn("liquidation subscriber failed: " + ex.Message); }
        }

        private BinanceFeatureAggregator[] GetAggregators(string symbolKey)
        {
            lock (_gate)
            {
                SymbolEntry symbol;
                if (!_symbols.TryGetValue(symbolKey, out symbol))
                    return new BinanceFeatureAggregator[0];
                return symbol.Aggregators.ToArray();
            }
        }

        private async Task StartConnectorAsync(IMarketDataConnector connector)
        {
            try { await connector.StartAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { HubLog.Error(connector.Name + " start: " + ex.Message); }
        }

        private async Task StopConnectorAsync(IMarketDataConnector connector)
        {
            try { await connector.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { HubLog.Warn(connector.Name + " stop: " + ex.Message); }
        }

        private sealed class SymbolEntry
        {
            public readonly string Symbol;
            public readonly List<BinanceFeatureAggregator> Aggregators =
                new List<BinanceFeatureAggregator>();
            public BinanceSpotConnector Spot;
            public BinanceFuturesConnector Futures;
            public bool RawLiquidationActive;

            public SymbolEntry(string symbol)
            {
                Symbol = symbol;
            }
        }

        private sealed class SeriesEntry
        {
            public readonly string Instrument;
            public readonly string Timeframe;
            public readonly SymbolEntry Symbol;
            public readonly BinanceFeatureAggregator Aggregator;

            public SeriesEntry(
                string instrument,
                string timeframe,
                SymbolEntry symbol,
                BinanceFeatureAggregator aggregator)
            {
                Instrument = instrument;
                Timeframe = timeframe;
                Symbol = symbol;
                Aggregator = aggregator;
            }
        }
    }
}
