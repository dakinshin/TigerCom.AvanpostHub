using System;
using System.Threading;
using System.Threading.Tasks;
using AvanpostHub.MarketData;

namespace AvanpostHub
{
    public sealed class HubRuntime : IDisposable
    {
        private HubSettings _settings = new HubSettings();
        private string _lastError = "";
        private CancellationTokenSource _calibrationCts;

        public HubRuntime()
        {
            Features = new FeatureStore();
            Storage = new HubStorage();
            Calibration = new OiFlowCalibrationStore();
            CalibrationClient = new ScreenerCalibrationClient();
            ScreenerHistory = new ScreenerOiHistoryClient();
            Server = new LoopbackServer(Features, ScreenerHistory, Storage, Calibration);
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
        public OiFlowCalibrationStore Calibration { get; }
        public ScreenerCalibrationClient CalibrationClient { get; }
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
                ScheduleCalibrationRefresh(_settings.ScreenerBaseUrl);
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
            var calibration = Calibration.GetProfile();
            status.CalibrationAvailable = calibration != null;
            status.CalibrationGeneratedAtMs = calibration == null ? 0 : calibration.GeneratedAtMs();
            status.Connectors = MarketData.ConnectorCount;
            status.LastError = string.IsNullOrWhiteSpace(_lastError) ? status.LastError : _lastError;
            return status;
        }

        public void Stop()
        {
            Server.Stop();
        }

        private void ScheduleCalibrationRefresh(string baseUrl)
        {
            var previous = _calibrationCts;
            _calibrationCts = new CancellationTokenSource();
            try { previous?.Cancel(); } catch { }
            try { previous?.Dispose(); } catch { }
            var token = _calibrationCts.Token;
            Task.Run(async () =>
            {
                try { await CalibrationClient.RefreshAsync(baseUrl, Calibration, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { HubLog.Warn("calibration refresh task failed: " + ex.Message); }
            }, token);
        }

        private void OnSnapshotPublished(FeatureSnapshot snapshot)
        {
            if (snapshot != null && snapshot.Final) Storage.AppendFeature(snapshot);
        }

        public void Dispose()
        {
            Features.SnapshotPublished -= OnSnapshotPublished;
            try { _calibrationCts?.Cancel(); } catch { }
            try { _calibrationCts?.Dispose(); } catch { }
            Server.Dispose();
            MarketData.Dispose();
            ScreenerHistory.Dispose();
            CalibrationClient.Dispose();
            Storage.Dispose();
        }
    }
}
