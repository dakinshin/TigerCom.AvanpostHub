using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AvanpostHub.MarketData
{
    /// <summary>
    /// Один биржевой WebSocket-поток с reconnect/backoff. Не знает, какие поля
    /// внутри JSON: отдаёт текст владельцу, который нормализует событие.
    /// </summary>
    public sealed class BinanceStreamLoop : IDisposable
    {
        private readonly string _name;
        private readonly Uri _uri;
        private readonly Action<string> _onText;
        private readonly Action<string> _log;
        private readonly object _gate = new object();
        private CancellationTokenSource _cts;
        private Task _task;
        private volatile bool _connected;
        private long _reconnects;
        private long _lastMessageMs;
        private volatile string _lastError = "";
        private long _received;

        public BinanceStreamLoop(string name, string url, Action<string> onText, Action<string> log = null)
        {
            _name = name;
            _uri = new Uri(url, UriKind.Absolute);
            _onText = onText ?? throw new ArgumentNullException(nameof(onText));
            _log = log ?? (_ => { });
        }

        public string Name => _name;
        public bool Connected => _connected;
        public long Received => Interlocked.Read(ref _received);

        public ConnectorStatus Status
        {
            get
            {
                return new ConnectorStatus
                {
                    Name = _name,
                    State = _connected ? "running" : "reconnecting",
                    LastMessageMs = Interlocked.Read(ref _lastMessageMs),
                    Reconnects = Interlocked.Read(ref _reconnects),
                    LastError = _lastError,
                };
            }
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (_task != null) return Task.CompletedTask;
                _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _task = RunAsync(_cts.Token);
                return Task.CompletedTask;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            Task task;
            CancellationTokenSource cts;
            lock (_gate)
            {
                cts = _cts;
                task = _task;
                _cts = null;
                _task = null;
            }

            try { cts?.Cancel(); } catch { }
            if (task != null)
            {
                try { await task.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log(_name + " stop: " + ex.Message); }
            }
            try { cts?.Dispose(); } catch { }
        }

        public void Dispose()
        {
            try { StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            var backoffMs = 1000;
            while (!cancellationToken.IsCancellationRequested)
            {
                using (var ws = new ClientWebSocket())
                {
                    try
                    {
                        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                        await ws.ConnectAsync(_uri, cancellationToken).ConfigureAwait(false);
                        backoffMs = 1000;
                        _connected = true;
                        _lastError = "";
                        Interlocked.Increment(ref _reconnects);
                        _log(_name + " connected: " + _uri);

                        var buffer = new byte[64 * 1024];
                        var text = new StringBuilder();
                        while (!cancellationToken.IsCancellationRequested && ws.State == WebSocketState.Open)
                        {
                            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                                .ConfigureAwait(false);
                            if (result.MessageType == WebSocketMessageType.Close)
                                break;

                            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                            if (!result.EndOfMessage) continue;

                            var message = text.ToString();
                            text.Clear();
                            Interlocked.Exchange(ref _lastMessageMs,
                                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                            Interlocked.Increment(ref _received);
                            try { _onText(message); }
                            catch (Exception ex) { _log(_name + " handler: " + ex.Message); }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Normal cancellation.
                    }
                    catch (Exception ex)
                    {
                        _lastError = ex.GetBaseException().Message;
                        _log(_name + " error: " + _lastError);
                    }
                    finally
                    {
                        _connected = false;
                    }
                }

                if (cancellationToken.IsCancellationRequested) break;
                try { await Task.Delay(backoffMs, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                backoffMs = Math.Min(backoffMs * 2, 30000);
            }
        }
    }
}
