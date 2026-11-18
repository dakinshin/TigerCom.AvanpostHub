//--------------------------------------------------------------------------------
// LoopbackServer — локальный HTTP/WebSocket сервер Avanpost Hub.
//
// Сделан на TcpListener, а не HttpListener, чтобы не требовать netsh urlacl
// и прав администратора. Слушает только 127.0.0.1.
//
// HTTP:
//   GET /health
//   GET /v1/status
//   GET /v1/features/latest?instrument=BINANCE:BTCUSDT:PERP&timeframe=1m
//   GET /v1/features/history?instrument=...&timeframe=1m&fromMs=...&limit=500
//
// WebSocket:
//   ws://127.0.0.1:7830/ws
//   {"type":"subscribe","instrument":"BINANCE:BTCUSDT:PERP","timeframe":"1m"}
//   {"type":"unsubscribe",...}
//   {"type":"history",...}
//   {"type":"status"}
//   {"type":"ping"}
//--------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace AvanpostHub
{
    public sealed class LoopbackServer : IDisposable
    {
        private const int MaxRequestBytes = 64 * 1024;
        private const int MaxFrameBytes = 1024 * 1024;
        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private const string LiquidationFeature = "liq.events.v1";
        private const string LiquidationTimeframe = "liq.events";

        private readonly FeatureStore _features;
        private readonly ScreenerOiHistoryClient _screenerHistory;
        private readonly HubStorage _storage;
        private readonly OiFlowCalibrationStore _calibration;
        private readonly NotificationService _notifications;
        private readonly object _gate = new object();
        private readonly ConcurrentDictionary<long, ClientSession> _clients =
            new ConcurrentDictionary<long, ClientSession>();
        private readonly object _subscriptionGate = new object();
        private readonly Dictionary<string, int> _subscriptionRefs =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _liquidationRefs =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private TcpListener _listener;
        private Thread _acceptThread;
        private CancellationTokenSource _cts;
        private long _nextClientId;
        private long _startedAtMs;
        private volatile bool _running;
        private volatile string _lastError = "";
        private int _port;

        public LoopbackServer(FeatureStore features, ScreenerOiHistoryClient screenerHistory = null,
            HubStorage storage = null, OiFlowCalibrationStore calibration = null,
            NotificationService notifications = null)
        {
            _features = features ?? throw new ArgumentNullException(nameof(features));
            _screenerHistory = screenerHistory;
            _storage = storage;
            _calibration = calibration;
            _notifications = notifications;
            if (_calibration != null) _calibration.Updated += OnCalibrationUpdated;
        }

        public event Action StatusChanged;
        public event Action<string, string> SubscriptionActivated;
        public event Action<string, string> SubscriptionDeactivated;
        public event Action<string> LiquidationSubscriptionActivated;
        public event Action<string> LiquidationSubscriptionDeactivated;

        public int Port => Volatile.Read(ref _port);
        public bool IsRunning => _running;
        public string LastError => _lastError;

        public void Start(int port)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

            lock (_gate)
            {
                if (_running) throw new InvalidOperationException("Server is already running.");

                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Server.Blocking = false;
                _listener = listener;
                _cts = new CancellationTokenSource();
                _startedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                Volatile.Write(ref _port, port);
                _lastError = "";
                _running = true;
                _acceptThread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "AvanpostHub.Accept",
                };
                _acceptThread.Start();
                _features.SnapshotPublished += OnSnapshotPublished;
                HubLog.Info("loopback server listening on 127.0.0.1:" + port);
            }

            NotifyStatus();
        }

        public void Stop()
        {
            Thread acceptThread;
            lock (_gate)
            {
                if (!_running && _listener == null) return;
                _running = false;
                try { _cts?.Cancel(); } catch { }
                acceptThread = _acceptThread;
                _acceptThread = null;
                _listener = null;
                _features.SnapshotPublished -= OnSnapshotPublished;
            }

            foreach (var client in _clients.Values.ToArray())
                RemoveClient(client);

            // Accept-loop работает в неблокирующем режиме и закрывает listener сам.
            if (acceptThread != null && acceptThread != Thread.CurrentThread)
                acceptThread.Join(1500);

            var cts = _cts;
            _cts = null;
            try { cts?.Dispose(); } catch { }
            HubLog.Info("loopback server stopped");
            NotifyStatus();
        }

        public HubStatus GetStatus()
        {
            var started = Interlocked.Read(ref _startedAtMs);
            var notificationStatus = _notifications == null ? null : _notifications.GetStatus();
            return new HubStatus
            {
                State = _running ? "running" : "stopped",
                Port = Port,
                Endpoint = "ws://127.0.0.1:" + Port + "/ws",
                Version = HubVersion.Current,
                StartedAtMs = started,
                UptimeMs = started <= 0 ? 0 : Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - started),
                Clients = _clients.Count,
                Subscriptions = SubscriptionRefCount + LiquidationRefCount,
                FeatureSeries = _features.SeriesCount,
                FeatureBars = _features.HistoryCount,
                Connectors = 0,
                NotificationsSent = notificationStatus == null ? 0 : notificationStatus.Sent,
                NotificationsFailed = notificationStatus == null ? 0 : notificationStatus.Failed,
                NotificationsQueued = notificationStatus == null ? 0 : notificationStatus.Queued,
                NotificationsEnabled = notificationStatus != null && notificationStatus.Enabled,
                NotificationsConfigured = notificationStatus != null && notificationStatus.Configured,
                NotificationLastError = notificationStatus == null ? "" : notificationStatus.LastError,
                LastError = _lastError,
            };
        }

        public void Dispose()
        {
            if (_calibration != null) _calibration.Updated -= OnCalibrationUpdated;
            Stop();
        }

        private int SubscriptionRefCount
        {
            get { lock (_subscriptionGate) return _subscriptionRefs.Count; }
        }

        private int LiquidationRefCount
        {
            get { lock (_subscriptionGate) return _liquidationRefs.Count; }
        }

        private void AcceptLoop()
        {
            var cts = _cts;
            var listener = _listener;
            try
            {
                while (cts != null && !cts.IsCancellationRequested)
                {
                    TcpClient tcp;
                    try
                    {
                        if (!listener.Server.Poll(100000, SelectMode.SelectRead)) continue;
                        tcp = listener.AcceptTcpClient();
                    }
                    catch (SocketException ex)
                    {
                        if (cts.IsCancellationRequested) break;
                        if (ex.SocketErrorCode == SocketError.WouldBlock ||
                            ex.SocketErrorCode == SocketError.Interrupted)
                            continue;
                        _lastError = ex.Message;
                        HubLog.Error("accept failed: " + ex.Message);
                        Thread.Sleep(100);
                        continue;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    try { tcp.Client.Blocking = true; tcp.NoDelay = true; } catch { }
                    var id = Interlocked.Increment(ref _nextClientId);
                    var session = new ClientSession(id, tcp);
                    ThreadPool.QueueUserWorkItem(_ => ServeClient(session));
                }
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        private void ServeClient(ClientSession session)
        {
            try
            {
                using (session.Tcp)
                using (var stream = session.Tcp.GetStream())
                {
                    session.Stream = stream;
                    var request = ReadHttpRequest(stream);
                    if (request == null) return;

                    if (request.IsWebSocket && request.Path == "/ws")
                    {
                        if (!UpgradeWebSocket(session, request)) return;
                        AddClient(session);
                        ServeWebSocket(session);
                        return;
                    }

                    ServeHttp(stream, request);
                }
            }
            catch (Exception ex)
            {
                if (_running) HubLog.Warn("client error: " + ex.Message);
            }
            finally
            {
                RemoveClient(session);
            }
        }

        private void ServeHttp(NetworkStream stream, HttpRequest request)
        {
            var query = ParseQuery(request.Query);
            var path = request.Path;

            if (path == "/" || path == "/health")
            {
                SendHttp(stream, 200, "application/json",
                    HubJson.Serialize(new
                    {
                        status = "ok",
                        name = "AvanpostHub",
                        version = HubVersion.Current,
                        uptimeMs = GetStatus().UptimeMs,
                    }));
                return;
            }

            if (path == "/v1/status")
            {
                SendHttp(stream, 200, "application/json", HubJson.Serialize(GetStatus()));
                return;
            }

            if (path == "/v1/calibration/oi-flow")
            {
                var profile = _calibration == null ? null : _calibration.GetProfile();
                if (profile == null)
                {
                    SendHttp(stream, 404, "application/json",
                        HubJson.Serialize(new { type = "error", message = "calibration profile not found" }));
                    return;
                }
                SendHttp(stream, 200, "application/json", HubJson.Serialize(profile));
                return;
            }

            if (path == "/v1/features/latest")
            {
                var instrument = Get(query, "instrument");
                var timeframe = Get(query, "timeframe", "1m");
                var snapshot = _features.GetLatest(instrument, timeframe);
                if (snapshot == null)
                {
                    SendHttp(stream, 404, "application/json",
                        HubJson.Serialize(new { type = "error", message = "feature snapshot not found" }));
                    return;
                }

                SendHttp(stream, 200, "application/json", HubJson.Serialize(snapshot));
                return;
            }

            if (path == "/v1/features/history")
            {
                var instrument = Get(query, "instrument");
                var timeframe = Get(query, "timeframe", "1m");
                long fromMs;
                long toMs;
                int limit;
                long.TryParse(Get(query, "fromMs", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out fromMs);
                if (!long.TryParse(Get(query, "toMs", long.MaxValue.ToString(CultureInfo.InvariantCulture)),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out toMs))
                    toMs = long.MaxValue;
                if (!int.TryParse(Get(query, "limit", "500"), NumberStyles.Integer, CultureInfo.InvariantCulture, out limit))
                    limit = 500;

                var rows = _features.GetHistory(instrument, timeframe, fromMs, toMs, limit);
                SendHttp(stream, 200, "application/json", HubJson.Serialize(new
                {
                    type = "history",
                    instrument,
                    timeframe,
                    rows,
                }));
                return;
            }

            if (request.Method == "GET" && path == "/v1/notifications/status")
            {
                var status = _notifications == null ? new NotificationStatus() : _notifications.GetStatus();
                SendHttp(stream, 200, "application/json", HubJson.Serialize(status));
                return;
            }

            if (request.Method == "POST" && path == "/v1/notifications")
            {
                if (_notifications == null)
                {
                    SendHttp(stream, 503, "application/json",
                        HubJson.Serialize(new { type = "error", message = "notifications are not available" }));
                    return;
                }

                NotificationMessage message;
                try { message = HubJson.Deserialize<NotificationMessage>(request.Body); }
                catch (Exception ex)
                {
                    SendHttp(stream, 400, "application/json",
                        HubJson.Serialize(new { type = "error", message = "invalid notification: " + ex.Message }));
                    return;
                }

                if (message == null)
                {
                    SendHttp(stream, 400, "application/json",
                        HubJson.Serialize(new { type = "error", message = "empty notification" }));
                    return;
                }

                var accepted = _notifications.Publish(message);
                if (!accepted.Queued)
                    HubLog.Info("notification skipped: " + (accepted.DedupeKey ?? "") + " — " +
                                (accepted.Reason ?? "not queued"));
                SendHttp(stream, accepted.Queued ? 202 : 200, "application/json", HubJson.Serialize(accepted));
                return;
            }

            SendHttp(stream, 404, "application/json",
                HubJson.Serialize(new { type = "error", message = "not found" }));
        }

        private bool UpgradeWebSocket(ClientSession session, HttpRequest request)
        {
            string key;
            if (!request.Headers.TryGetValue("Sec-WebSocket-Key", out key) || string.IsNullOrWhiteSpace(key))
                return false;

            var accept = Convert.ToBase64String(SHA1.Create().ComputeHash(
                Encoding.ASCII.GetBytes(key.Trim() + WebSocketGuid)));

            var header = "HTTP/1.1 101 Switching Protocols\r\n" +
                         "Upgrade: websocket\r\n" +
                         "Connection: Upgrade\r\n" +
                         "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(header);
            lock (session.WriteGate) session.Stream.Write(bytes, 0, bytes.Length);
            return true;
        }

        private void ServeWebSocket(ClientSession session)
        {
            session.SendText(HubJson.Serialize(new
            {
                type = "hello",
                protocol = 1,
                version = HubVersion.Current,
                hub = "AvanpostHub",
                featureSets = new[] { "oi.v1", "spot.v1", "liq.v1", "context.v1", "calibration.v1" },
            }));
            session.SendText(HubJson.Serialize(new { type = "status", status = GetStatus() }));

            var message = new MemoryStream();
            while (_running)
            {
                int opcode;
                bool fin;
                byte[] payload;
                if (!ReadFrame(session.Stream, out opcode, out fin, out payload)) break;

                if (opcode == 0x8)
                {
                    TrySendFrame(session.Stream, 0x8, new byte[0], session.WriteGate);
                    break;
                }

                if (opcode == 0x9)
                {
                    TrySendFrame(session.Stream, 0xA, payload ?? new byte[0], session.WriteGate);
                    continue;
                }

                if (opcode == 0xA) continue;
                if (opcode != 0x1 && opcode != 0x0) break;

                if (payload != null && payload.Length > 0) message.Write(payload, 0, payload.Length);
                if (!fin) continue;

                var text = Encoding.UTF8.GetString(message.ToArray());
                message.SetLength(0);
                HandleWebSocketMessage(session, text);
            }
        }

        private void HandleWebSocketMessage(ClientSession session, string text)
        {
            HubRequest request;
            try { request = HubJson.Deserialize<HubRequest>(text); }
            catch (Exception ex)
            {
                SendError(session, "invalid JSON: " + ex.Message);
                return;
            }

            var type = (request == null ? null : request.Type ?? "").Trim().ToLowerInvariant();
            if (type.Length == 0)
            {
                SendError(session, "missing type");
                return;
            }

            if (type == "ping")
            {
                session.SendText(HubJson.Serialize(new { type = "pong" }));
                return;
            }

            if (type == "status")
            {
                session.SendText(HubJson.Serialize(new { type = "status", status = GetStatus() }));
                return;
            }

            if (type == "oicalibration")
            {
                var profile = _calibration == null ? null : _calibration.GetProfile();
                if (profile == null)
                {
                    session.SendText(HubJson.Serialize(new
                    {
                        type = "oiCalibration",
                        available = false,
                        error = _calibration == null ? "calibration store unavailable" : _calibration.LastError,
                    }));
                    return;
                }
                session.SendText(HubJson.Serialize(new
                {
                    type = "oiCalibration",
                    available = true,
                    profile,
                }));
                return;
            }

            if (type == "subscribe")
            {
                var instrument = Normalize(request.Instrument);
                var timeframe = NormalizeTimeframe(request.Timeframe);
                if (instrument.Length == 0)
                {
                    SendError(session, "instrument is required");
                    return;
                }

                var feature = (request.Feature ?? "").Trim();
                if (string.Equals(feature, LiquidationFeature, StringComparison.OrdinalIgnoreCase))
                {
                    var liquidationKey = FeatureStore.Key(instrument, LiquidationTimeframe);
                    if (session.AddSubscription(liquidationKey)) AddLiquidationRef(instrument);
                    session.SendText(HubJson.Serialize(new
                    {
                        type = "subscribed",
                        instrument,
                        feature = LiquidationFeature,
                    }));
                    return;
                }

                var key = FeatureStore.Key(instrument, timeframe);
                if (session.AddSubscription(key)) AddSubscriptionRef(key);
                session.SendText(HubJson.Serialize(new
                {
                    type = "subscribed",
                    instrument,
                    timeframe,
                }));

                var latest = _features.GetLatest(instrument, timeframe);
                if (latest != null) session.SendText(HubJson.Serialize(latest));
                return;
            }

            if (type == "unsubscribe")
            {
                var instrument = Normalize(request.Instrument);
                var timeframe = NormalizeTimeframe(request.Timeframe);
                var feature = (request.Feature ?? "").Trim();
                if (string.Equals(feature, LiquidationFeature, StringComparison.OrdinalIgnoreCase))
                {
                    var liquidationKey = FeatureStore.Key(instrument, LiquidationTimeframe);
                    if (session.RemoveSubscription(liquidationKey)) RemoveLiquidationRef(instrument);
                    session.SendText(HubJson.Serialize(new
                    {
                        type = "unsubscribed",
                        instrument,
                        feature = LiquidationFeature,
                    }));
                    return;
                }

                var key = FeatureStore.Key(instrument, timeframe);
                if (session.RemoveSubscription(key)) RemoveSubscriptionRef(key);
                session.SendText(HubJson.Serialize(new { type = "unsubscribed", instrument, timeframe }));
                return;
            }

            if (type == "oihistory")
            {
                var instrument = Normalize(request.Instrument);
                var timeframe = NormalizeTimeframe(request.Timeframe);
                if (instrument.Length == 0)
                {
                    SendError(session, "instrument is required");
                    return;
                }
                if (_screenerHistory == null)
                {
                    SendError(session, "screener OI history is not configured");
                    return;
                }

                try
                {
                    var limit = request.Limit ?? 500;
                    var message = _screenerHistory.Fetch(instrument, timeframe, limit);
                    var merged = MergeOiHistory(instrument, timeframe, message, limit);
                    session.SendText(HubJson.Serialize(merged));
                }
                catch (Exception ex)
                {
                    SendError(session, "OI history failed: " + ex.Message);
                }
                return;
            }

            if (type == "history")
            {
                var instrument = Normalize(request.Instrument);
                var timeframe = NormalizeTimeframe(request.Timeframe);
                if (instrument.Length == 0)
                {
                    SendError(session, "instrument is required");
                    return;
                }

                var rows = _features.GetHistory(
                    instrument,
                    timeframe,
                    request.FromMs ?? 0,
                    request.ToMs ?? long.MaxValue,
                    request.Limit ?? 500);
                session.SendText(HubJson.Serialize(new
                {
                    type = "history",
                    instrument,
                    timeframe,
                    rows,
                }));
                return;
            }

            SendError(session, "unknown type: " + type);
        }

        private OiHistoryMessage MergeOiHistory(string instrument, string timeframe, OiHistoryMessage fresh, int limit)
        {
            var all = new Dictionary<long, OiHistoryPoint>();
            if (_storage != null)
            {
                foreach (var point in _storage.LoadOi(instrument, timeframe, 0, long.MaxValue, 5000))
                    all[point.T] = new OiHistoryPoint { T = point.T, Oi = point.Oi };
            }
            if (fresh != null && fresh.Points != null)
            {
                foreach (var point in fresh.Points)
                    all[point.T] = new OiHistoryPoint { T = point.T, Oi = point.Oi };
            }

            var merged = new OiHistoryMessage
            {
                Instrument = instrument,
                Timeframe = timeframe,
                Period = fresh == null ? "" : fresh.Period,
                MinutePoints = fresh == null ? 0 : fresh.MinutePoints,
                Live = fresh == null ? null : fresh.Live,
                NextEndMs = fresh == null ? null : fresh.NextEndMs,
                FetchedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Source = fresh != null && fresh.Points != null && fresh.Points.Count > 0 ? "SCREENER+HUB" : "HUB",
                Points = all.Values.OrderBy(x => x.T).ToList(),
            };
            if (merged.Points.Count > limit)
                merged.Points = merged.Points.Skip(merged.Points.Count - limit).ToList();
            merged.Available = merged.Points.Count > 0;

            if (_storage != null && fresh != null && fresh.Points != null && fresh.Points.Count > 0)
            {
                var granularity = PeriodMinutes(fresh.Period);
                _storage.AppendOi(instrument, timeframe, fresh.Points.Select(x => new StoredOiPoint
                {
                    T = x.T,
                    Oi = x.Oi,
                    Source = "SCREENER",
                    GranularityMin = granularity,
                }));
            }

            return merged;
        }

        private static int PeriodMinutes(string period)
        {
            var value = (period ?? "").Trim().ToLowerInvariant();
            if (value.Length < 2) return 0;
            int amount;
            if (!int.TryParse(value.Substring(0, value.Length - 1), out amount) || amount <= 0) return 0;
            switch (value[value.Length - 1])
            {
                case 'm': return amount;
                case 'h': return amount * 60;
                case 'd': return amount * 1440;
                default: return 0;
            }
        }

        public void PublishLiquidation(string instrument, MarketData.BinanceLiquidation liquidation)
        {
            var normalized = Normalize(instrument);
            var key = FeatureStore.Key(normalized, LiquidationTimeframe);
            var message = new LiquidationMessage
            {
                Instrument = normalized,
                TimeMs = liquidation.TimeMs,
                IsLong = liquidation.IsLong,
                Price = liquidation.Price,
                Quantity = liquidation.Quantity,
                Notional = liquidation.Notional,
                Source = "BINANCE",
            };
            var json = HubJson.Serialize(message);
            foreach (var client in _clients.Values)
            {
                if (!client.HasSubscription(key)) continue;
                if (!client.SendText(json)) RemoveClient(client);
            }
        }

        private void OnCalibrationUpdated(OiFlowCalibrationProfile profile)
        {
            if (profile == null) return;
            var json = HubJson.Serialize(new
            {
                type = "oiCalibration",
                available = true,
                profile,
            });
            foreach (var client in _clients.Values)
            {
                if (!client.SendText(json)) RemoveClient(client);
            }
        }

        private void OnSnapshotPublished(FeatureSnapshot snapshot)
        {
            if (snapshot == null) return;
            var key = FeatureStore.Key(snapshot.Instrument, snapshot.Timeframe);
            var json = HubJson.Serialize(snapshot);
            foreach (var client in _clients.Values)
            {
                if (!client.HasSubscription(key)) continue;
                if (!client.SendText(json)) RemoveClient(client);
            }
        }

        private void AddClient(ClientSession session)
        {
            _clients[session.Id] = session;
            NotifyStatus();
        }

        private void RemoveClient(ClientSession session)
        {
            if (session == null) return;
            ClientSession ignored;
            _clients.TryRemove(session.Id, out ignored);
            var keys = session.ClearSubscriptions();
            foreach (var key in keys)
            {
                var split = key.LastIndexOf('|');
                if (split > 0 && split < key.Length - 1 &&
                    string.Equals(key.Substring(split + 1), LiquidationTimeframe, StringComparison.OrdinalIgnoreCase))
                    RemoveLiquidationRef(key.Substring(0, split));
                else
                    RemoveSubscriptionRef(key);
            }
            session.Dispose();
            if (_running) NotifyStatus();
        }

        private void AddSubscriptionRef(string key)
        {
            var activate = false;
            lock (_subscriptionGate)
            {
                int count;
                _subscriptionRefs.TryGetValue(key, out count);
                _subscriptionRefs[key] = count + 1;
                activate = count == 0;
            }

            if (activate) RaiseSubscriptionEvent(key, true);
        }

        private void RemoveSubscriptionRef(string key)
        {
            var deactivate = false;
            lock (_subscriptionGate)
            {
                int count;
                if (!_subscriptionRefs.TryGetValue(key, out count)) return;
                if (count <= 1)
                {
                    _subscriptionRefs.Remove(key);
                    deactivate = true;
                }
                else
                {
                    _subscriptionRefs[key] = count - 1;
                }
            }

            if (deactivate) RaiseSubscriptionEvent(key, false);
        }

        private void AddLiquidationRef(string instrument)
        {
            var activate = false;
            lock (_subscriptionGate)
            {
                int count;
                _liquidationRefs.TryGetValue(instrument, out count);
                _liquidationRefs[instrument] = count + 1;
                activate = count == 0;
            }

            if (activate) RaiseLiquidationSubscriptionEvent(instrument, true);
        }

        private void RemoveLiquidationRef(string instrument)
        {
            var deactivate = false;
            lock (_subscriptionGate)
            {
                int count;
                if (!_liquidationRefs.TryGetValue(instrument, out count)) return;
                if (count <= 1)
                {
                    _liquidationRefs.Remove(instrument);
                    deactivate = true;
                }
                else
                {
                    _liquidationRefs[instrument] = count - 1;
                }
            }

            if (deactivate) RaiseLiquidationSubscriptionEvent(instrument, false);
        }

        private void RaiseLiquidationSubscriptionEvent(string instrument, bool activated)
        {
            try
            {
                if (activated) LiquidationSubscriptionActivated?.Invoke(instrument);
                else LiquidationSubscriptionDeactivated?.Invoke(instrument);
            }
            catch (Exception ex)
            {
                HubLog.Warn("liquidation subscription callback failed: " + ex.Message);
            }
        }

        private void RaiseSubscriptionEvent(string key, bool activated)
        {
            var split = key.LastIndexOf('|');
            if (split <= 0 || split >= key.Length - 1) return;
            var instrument = key.Substring(0, split);
            var timeframe = key.Substring(split + 1);
            try
            {
                if (activated) SubscriptionActivated?.Invoke(instrument, timeframe);
                else SubscriptionDeactivated?.Invoke(instrument, timeframe);
            }
            catch (Exception ex)
            {
                HubLog.Warn("subscription callback failed: " + ex.Message);
            }
        }

        private void NotifyStatus()
        {
            try { StatusChanged?.Invoke(); } catch { }
        }

        private static void SendError(ClientSession session, string message)
        {
            session.SendText(HubJson.Serialize(new { type = "error", message }));
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant();
        }

        private static string NormalizeTimeframe(string value)
        {
            var s = string.IsNullOrWhiteSpace(value) ? "1m" : value.Trim();
            return s.Length == 0 ? "1m" : s;
        }

        private static HttpRequest ReadHttpRequest(NetworkStream stream)
        {
            var buffer = new MemoryStream();
            var temp = new byte[4096];
            var headerEnd = -1;
            while (buffer.Length < MaxRequestBytes)
            {
                var n = stream.Read(temp, 0, temp.Length);
                if (n <= 0) return null;
                buffer.Write(temp, 0, n);
                headerEnd = FindHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
                if (headerEnd >= 0) break;
            }
            if (headerEnd < 0) return null;

            var headerText = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd);
            var request = ParseHttpRequest(headerText);
            if (request == null) return null;

            var contentLength = 0;
            string contentLengthText;
            if (request.Headers.TryGetValue("Content-Length", out contentLengthText) &&
                !int.TryParse(contentLengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength))
                return null;
            if (contentLength < 0 || contentLength > MaxRequestBytes - (headerEnd + 4)) return null;

            var bodyOffset = headerEnd + 4;
            while (buffer.Length < bodyOffset + contentLength)
            {
                var n = stream.Read(temp, 0, temp.Length);
                if (n <= 0) return null;
                buffer.Write(temp, 0, n);
                if (buffer.Length > MaxRequestBytes) return null;
            }

            if (contentLength > 0)
                request.Body = Encoding.UTF8.GetString(buffer.GetBuffer(), bodyOffset, contentLength);
            return request;
        }

        private static int FindHeaderEnd(byte[] bytes, int length)
        {
            if (bytes == null || length < 4) return -1;
            for (var i = 3; i < length; i++)
            {
                if (bytes[i - 3] == (byte)'\r' && bytes[i - 2] == (byte)'\n' &&
                    bytes[i - 1] == (byte)'\r' && bytes[i] == (byte)'\n')
                    return i - 3;
            }
            return -1;
        }

        private static HttpRequest ParseHttpRequest(string header)
        {
            var lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;

            var target = first[1];
            var path = target;
            var query = "";
            var q = target.IndexOf('?');
            if (q >= 0)
            {
                path = target.Substring(0, q);
                query = target.Substring(q + 1);
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < lines.Length; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            string upgrade;
            string connection;
            headers.TryGetValue("Upgrade", out upgrade);
            headers.TryGetValue("Connection", out connection);
            var isWs = string.Equals(upgrade, "websocket", StringComparison.OrdinalIgnoreCase) &&
                       (connection ?? "").IndexOf("Upgrade", StringComparison.OrdinalIgnoreCase) >= 0;

            return new HttpRequest
            {
                Method = first[0],
                Path = path,
                Query = query,
                Headers = headers,
                IsWebSocket = isWs,
            };
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(query)) return dict;
            foreach (var part in query.Split('&'))
            {
                if (part.Length == 0) continue;
                var eq = part.IndexOf('=');
                var key = eq < 0 ? part : part.Substring(0, eq);
                var value = eq < 0 ? "" : part.Substring(eq + 1);
                dict[Uri.UnescapeDataString(key.Replace("+", " "))] =
                    Uri.UnescapeDataString(value.Replace("+", " "));
            }
            return dict;
        }

        private static string Get(Dictionary<string, string> query, string key, string fallback = "")
        {
            string value;
            return query != null && query.TryGetValue(key, out value) ? value : fallback;
        }

        private static void SendHttp(NetworkStream stream, int status, string contentType, string body)
        {
            var reason = status == 200 ? "OK" : (status == 404 ? "Not Found" : "Error");
            var bytes = Encoding.UTF8.GetBytes(body ?? "");
            var header = "HTTP/1.1 " + status + " " + reason + "\r\n" +
                         "Content-Type: " + contentType + "; charset=utf-8\r\n" +
                         "Content-Length: " + bytes.Length + "\r\n" +
                         "Cache-Control: no-store\r\n" +
                         "Connection: close\r\n\r\n";
            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (bytes.Length > 0) stream.Write(bytes, 0, bytes.Length);
        }

        private static bool ReadFrame(NetworkStream stream, out int opcode, out bool fin, out byte[] payload)
        {
            opcode = 0;
            fin = false;
            payload = null;

            var head = ReadExactly(stream, 2);
            if (head == null) return false;
            fin = (head[0] & 0x80) != 0;
            opcode = head[0] & 0x0F;
            var masked = (head[1] & 0x80) != 0;
            long length = head[1] & 0x7F;

            if (length == 126)
            {
                var ext = ReadExactly(stream, 2);
                if (ext == null) return false;
                length = (ext[0] << 8) | ext[1];
            }
            else if (length == 127)
            {
                var ext = ReadExactly(stream, 8);
                if (ext == null) return false;
                length = 0;
                for (var i = 0; i < 8; i++) length = (length << 8) | ext[i];
            }

            if (length < 0 || length > MaxFrameBytes) return false;
            var mask = masked ? ReadExactly(stream, 4) : null;
            if (masked && mask == null) return false;
            payload = length == 0 ? new byte[0] : ReadExactly(stream, (int)length);
            if (payload == null) return false;
            if (masked)
            {
                for (var i = 0; i < payload.Length; i++)
                    payload[i] ^= mask[i % 4];
            }
            return true;
        }

        private static byte[] ReadExactly(NetworkStream stream, int count)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                int n;
                try { n = stream.Read(buffer, offset, count - offset); }
                catch { return null; }
                if (n <= 0) return null;
                offset += n;
            }
            return buffer;
        }

        private static bool TrySendFrame(NetworkStream stream, int opcode, byte[] payload, object gate)
        {
            try
            {
                payload = payload ?? new byte[0];
                using (var ms = new MemoryStream())
                {
                    ms.WriteByte((byte)(0x80 | opcode));
                    if (payload.Length < 126)
                    {
                        ms.WriteByte((byte)payload.Length);
                    }
                    else if (payload.Length < 65536)
                    {
                        ms.WriteByte(126);
                        ms.WriteByte((byte)(payload.Length >> 8));
                        ms.WriteByte((byte)(payload.Length & 0xFF));
                    }
                    else
                    {
                        ms.WriteByte(127);
                        for (var i = 7; i >= 0; i--)
                            ms.WriteByte((byte)((long)payload.Length >> (8 * i) & 0xFF));
                    }

                    ms.Write(payload, 0, payload.Length);
                    var frame = ms.ToArray();
                    lock (gate) stream.Write(frame, 0, frame.Length);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private sealed class HttpRequest
        {
            public string Method;
            public string Path;
            public string Query;
            public Dictionary<string, string> Headers;
            public string Body = "";
            public bool IsWebSocket;
        }

        private sealed class ClientSession : IDisposable
        {
            private readonly HashSet<string> _subscriptions =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly object _subscriptionGate = new object();

            public readonly long Id;
            public readonly TcpClient Tcp;
            public readonly object WriteGate = new object();
            public NetworkStream Stream;
            private bool _disposed;

            public ClientSession(long id, TcpClient tcp)
            {
                Id = id;
                Tcp = tcp;
            }

            public int SubscriptionCount
            {
                get { lock (_subscriptionGate) return _subscriptions.Count; }
            }

            public bool AddSubscription(string key)
            {
                lock (_subscriptionGate) return _subscriptions.Add(key);
            }

            public bool RemoveSubscription(string key)
            {
                lock (_subscriptionGate) return _subscriptions.Remove(key);
            }

            public string[] ClearSubscriptions()
            {
                lock (_subscriptionGate)
                {
                    if (_subscriptions.Count == 0) return new string[0];
                    var keys = new string[_subscriptions.Count];
                    _subscriptions.CopyTo(keys);
                    _subscriptions.Clear();
                    return keys;
                }
            }

            public bool HasSubscription(string key)
            {
                lock (_subscriptionGate) return _subscriptions.Contains(key);
            }

            public bool SendText(string text)
            {
                if (_disposed || Stream == null) return false;
                return TrySendFrame(Stream, 0x1, Encoding.UTF8.GetBytes(text ?? ""), WriteGate);
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try { Tcp.Close(); } catch { }
            }
        }
    }
}
