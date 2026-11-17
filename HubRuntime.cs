using System;

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
        }

        public FeatureStore Features { get; }
        public LoopbackServer Server { get; }
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
            status.Connectors = 0;
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
        }
    }
}
