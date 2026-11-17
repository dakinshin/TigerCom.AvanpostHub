using System;
using System.Threading;
using System.Threading.Tasks;

namespace AvanpostHub.MarketData
{
    public sealed class BinanceSpotConnector : IMarketDataConnector
    {
        private readonly string _symbol;
        private readonly BinanceStreamLoop _tradeLoop;

        public BinanceSpotConnector(string symbol, Action<string> log = null)
        {
            _symbol = (symbol ?? "").Trim().ToUpperInvariant();
            _tradeLoop = new BinanceStreamLoop(
                "BINANCE:" + _symbol + ":SPOT:aggTrade",
                "wss://stream.binance.com:9443/ws/" + _symbol.ToLowerInvariant() + "@aggTrade",
                OnText,
                log);
        }

        public event Action<BinanceTrade> Trade;

        public string Name => "BINANCE:" + _symbol + ":SPOT";
        public ConnectorStatus Status => _tradeLoop.Status;

        public Task StartAsync(CancellationToken cancellationToken) =>
            _tradeLoop.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken) =>
            _tradeLoop.StopAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _tradeLoop.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private void OnText(string json)
        {
            BinanceTrade trade;
            if (!BinanceJson.TryParseTrade(json, out trade)) return;
            trade.IsSpot = true;
            Trade?.Invoke(trade);
        }
    }
}
