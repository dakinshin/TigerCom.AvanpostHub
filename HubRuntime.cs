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
            Storage = new HubStorage();
            ScreenerHistory = new ScreenerOiHistoryClient();
            Server = new LoopbackServer(Features, ScreenerHistory, Storage);
            MarketData = new MarketDataCoordinator(Features);
            Features.SnapshotPublished += OnSnapshotPublished;
            Server.SubscriptionActivated += MarketData.Activate;
            Server.SubscriptionDeactivated += MarketData.Deactivate;
            Server.LiquidationSubscriptionActivated += MarketData.ActivateLiquidations;
            Server.LiquidationSubscriptionDeactivated += MarketData.DeactivateLiquidations;
            MarketData.LiquidationReceived += Server.PublishLiquidation;
        }

        public FeatureStore Features { get; }
        public HubStorage Storage { get; }
        public ScreenerOiHistoryClient ScreenerHistory { get; }
        public LoopbackServer Server { get; }
        public MarketDataCoordinator MarketData { get; }
        public HubSettings Settings => _settings;

        public string Restart(HubSettings settings)
        {
            _settings = (settings ?? new HubSettings()).Sanitized();
            ScreenerHistory.Configure(_settings.ScreenerBaseUrl);
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
            status.StoredOiPoints = Storage.LoadedOiPoints;
            status.StoredFeatureBars = Storage.LoadedFeatureBars;
            status.Connectors = MarketData.ConnectorCount;
            status.LastError = string.IsNullOrWhiteSpace(_lastError) ? status.LastError : _lastError;
            return status;
        }

        public void Stop()
        {
            Server.Stop();
        }

        private void OnSnapshotPublished(FeatureSnapshot snapshot)
        {
            if (snapshot != null && snapshot.Final) Storage.AppendFeature(snapshot);
        }

        public void Dispose()
        {
            Features.SnapshotPublished -= OnSnapshotPublished;
            Server.Dispose();
            MarketData.Dispose();
            ScreenerHistory.Dispose();
            Storage.Dispose();
        }
    }
}
