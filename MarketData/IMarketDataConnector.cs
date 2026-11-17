using System;
using System.Threading;
using System.Threading.Tasks;

namespace AvanpostHub.MarketData
{
    /// <summary>
    /// Общий контракт биржевого адаптера. Hub не знает деталей Binance/Bybit/OKX:
    /// адаптер нормализует инструменты и публикует признаки в FeatureStore.
    /// </summary>
    public interface IMarketDataConnector : IAsyncDisposable
    {
        string Name { get; }
        ConnectorStatus Status { get; }
        Task StartAsync(CancellationToken cancellationToken);
        Task StopAsync(CancellationToken cancellationToken);
    }
}
