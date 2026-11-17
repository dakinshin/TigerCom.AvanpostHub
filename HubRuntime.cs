using System;
using AvanpostHub.MarketData;

namespace AvanpostHub
{
    public sealed class HubRuntime : IDisposable
    {
        private HubSettings _settings = new HubSettings();
        private string _lastError = "";

        public HubRuntime()
        {
            Features = new FeatureStore();
            Server = new LoopbackServer(Features);
            MarketData = new MarketDataCoordinator(Features);
            Server.SubscriptionActivated += MarketData.Activate;
            Server.SubscriptionDeactivated += MarketData.Deactivate;
            Server.LiquidationSubscriptionActivated += MarketData.ActivateLiquidations;
            Server.LiquidationSubscriptionDeactivated += MarketData.DeactivateLiquidations;
            MarketData.LiquidationReceived += Server.PublishLiquidation;
        }

        public FeatureStore Features { get; }
        public LoopbackServer Server { get; }
        public MarketDataCoordinator MarketData { get; }
        public HubSettings Settings => _settings;

        public string Restart(HubSettings settings)
        {
            _settings = (settings ?? new HubSettings()).Sanitized();
            try
            {
                Server.Stop();
                Server.Start(_settings.Port);
                _lastError = "";
                return null;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                HubLog.Error("hub start failed: " + ex.Message);
                return ex.Message;
            }
        }

        public HubStatus GetStatus()
        {
            var status = Server.GetStatus();
            status.FeatureSeries = Features.SeriesCount;
            status.FeatureBars = Features.HistoryCount;
            status.Connectors = MarketData.ConnectorCount;
            status.LastError = string.IsNullOrWhiteSpace(_lastError) ? status.LastError : _lastError;
            return status;
        }

        public void Stop()
        {
            Server.Stop();
        }

        public void Dispose()
        {
            Server.Dispose();
            MarketData.Dispose();
        }
    }
}
