using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AvanpostHub.MarketData
{
    public sealed class BinanceFuturesConnector : IMarketDataConnector
    {
        private readonly string _symbol;
        private readonly HttpClient _http;
        private readonly BinanceStreamLoop _tradeLoop;
        private readonly BinanceStreamLoop _markLoop;
        private readonly BinanceStreamLoop _liquidationLoop;
        private CancellationTokenSource _pollCts = new CancellationTokenSource();
        private Task _pollTask;

        public BinanceFuturesConnector(string symbol, Action<string> log = null)
        {
            _symbol = (symbol ?? "").Trim().ToUpperInvariant();
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var lower = _symbol.ToLowerInvariant();
            var host = "wss://fstream.binance.com/market/ws/";

            _tradeLoop = new BinanceStreamLoop(
                "BINANCE:" + _symbol + ":PERP:aggTrade",
                host + lower + "@aggTrade",
                OnTradeText,
                log);
            _markLoop = new BinanceStreamLoop(
                "BINANCE:" + _symbol + ":PERP:markPrice",
                host + lower + "@markPrice@1s",
                OnMarkText,
                log);
            _liquidationLoop = new BinanceStreamLoop(
                "BINANCE:" + _symbol + ":PERP:forceOrder",
                host + lower + "@forceOrder",
                OnLiquidationText,
                log);
        }

        public event Action<BinanceTrade> Trade;
        public event Action<BinanceMarkPrice> MarkPrice;
        public event Action<BinanceOpenInterest> OpenInterest;
        public event Action<BinanceLiquidation> Liquidation;

        public string Name => "BINANCE:" + _symbol + ":PERP";
        public ConnectorStatus Status
        {
            get
            {
                var trade = _tradeLoop.Status;
                var mark = _markLoop.Status;
                var liq = _liquidationLoop.Status;
                return new ConnectorStatus
                {
                    Name = Name,
                    State = trade.State == "running" || mark.State == "running" || liq.State == "running"
                        ? "running"
                        : "reconnecting",
                    LastMessageMs = Math.Max(trade.LastMessageMs, Math.Max(mark.LastMessageMs, liq.LastMessageMs)),
                    Reconnects = trade.Reconnects + mark.Reconnects + liq.Reconnects,
                    LastError = FirstError(trade.LastError, mark.LastError, liq.LastError),
                };
            }
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            await _tradeLoop.StartAsync(cancellationToken).ConfigureAwait(false);
            await _markLoop.StartAsync(cancellationToken).ConfigureAwait(false);
            await _liquidationLoop.StartAsync(cancellationToken).ConfigureAwait(false);
            if (_pollTask == null)
            {
                if (_pollCts.IsCancellationRequested)
                {
                    _pollCts.Dispose();
                    _pollCts = new CancellationTokenSource();
                }
                _pollTask = PollOpenInterestAsync(_pollCts.Token);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try { _pollCts.Cancel(); } catch { }
            await _tradeLoop.StopAsync(cancellationToken).ConfigureAwait(false);
            await _markLoop.StopAsync(cancellationToken).ConfigureAwait(false);
            await _liquidationLoop.StopAsync(cancellationToken).ConfigureAwait(false);
            if (_pollTask != null)
            {
                try { await _pollTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception) { }
                _pollTask = null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            _pollCts.Dispose();
            _http.Dispose();
        }

        private void OnTradeText(string json)
        {
            BinanceTrade trade;
            if (!BinanceJson.TryParseTrade(json, out trade)) return;
            trade.IsSpot = false;
            Trade?.Invoke(trade);
        }

        private void OnMarkText(string json)
        {
            BinanceMarkPrice mark;
            if (BinanceJson.TryParseMarkPrice(json, out mark)) MarkPrice?.Invoke(mark);
        }

        private void OnLiquidationText(string json)
        {
            BinanceLiquidation liquidation;
            if (BinanceJson.TryParseLiquidation(json, out liquidation)) Liquidation?.Invoke(liquidation);
        }

        private async Task PollOpenInterestAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var url = "https://fapi.binance.com/fapi/v1/openInterest?symbol=" + _symbol;
                    var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                    BinanceOpenInterest oi;
                    if (BinanceJson.TryParseOpenInterest(json, out oi))
                    {
                        oi.Symbol = _symbol;
                        OpenInterest?.Invoke(oi);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    HubLog.Warn("BINANCE OI poll " + _symbol + ": " + ex.Message);
                }

                try { await Task.Delay(5000, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private static string FirstError(params string[] errors)
        {
            foreach (var error in errors)
                if (!string.IsNullOrWhiteSpace(error)) return error;
            return "";
        }
    }
}
