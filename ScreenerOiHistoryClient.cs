using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AvanpostHub.MarketData;

namespace AvanpostHub
{
    public sealed class OiHistoryPoint
    {
        public long T { get; set; }
        public double Oi { get; set; }
    }

    public sealed class OiHistoryLive
    {
        public double Oi { get; set; }
        public long TsMs { get; set; }
    }

    public sealed class OiHistoryMessage
    {
        public string Type { get; set; } = "oiHistory";
        public string Instrument { get; set; } = "";
        public string Timeframe { get; set; } = "";
        public string Period { get; set; } = "";
        public bool Available { get; set; }
        public List<OiHistoryPoint> Points { get; set; } = new List<OiHistoryPoint>();
        public OiHistoryLive Live { get; set; }
        public int MinutePoints { get; set; }
        public long? NextEndMs { get; set; }
        public long FetchedAtMs { get; set; }
        public string Source { get; set; } = "SCREENER";
        public string Error { get; set; }
    }

    /// <summary>
    /// Клиент истории OI из screener API. История нужна только для warm-up:
    /// live OI остаётся из Binance/Hub, а внешние точки заполняют пропуски
    /// в исторических барах терминала.
    /// </summary>
    public sealed class ScreenerOiHistoryClient : IDisposable
    {
        private static readonly string[] Quotes = { "USDT", "USDC", "FDUSD", "BUSD", "USD", "BTC", "ETH", "BNB" };
        private readonly HttpClient _http;
        private readonly object _gate = new object();
        private readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private string _baseUrl = "https://avanpost.io";

        public ScreenerOiHistoryClient()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("AvanpostHub/0.2");
        }

        public string BaseUrl
        {
            get { lock (_gate) return _baseUrl; }
        }

        public void Configure(string baseUrl)
        {
            var value = (baseUrl ?? "").Trim().TrimEnd('/');
            if (value.Length == 0) value = "https://avanpost.io";
            lock (_gate)
            {
                if (string.Equals(_baseUrl, value, StringComparison.OrdinalIgnoreCase)) return;
                _baseUrl = value;
                _cache.Clear();
            }
        }

        public OiHistoryMessage Fetch(string instrument, string timeframe, int limit)
        {
            return FetchAsync(instrument, timeframe, limit, CancellationToken.None).GetAwaiter().GetResult();
        }

        public async Task<OiHistoryMessage> FetchAsync(string instrument, string timeframe, int limit, CancellationToken cancellationToken)
        {
            var message = new OiHistoryMessage
            {
                Instrument = (instrument ?? "").Trim().ToUpperInvariant(),
                Timeframe = NormalizeTimeframe(timeframe),
                FetchedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            if (limit < 2) limit = 2;
            if (limit > 500) limit = 500;

            BinanceInstrument parsed;
            if (!BinanceInstrument.TryParse(message.Instrument, out parsed) ||
                parsed.Market != BinanceMarketType.Perpetual)
            {
                message.Error = "only BINANCE perpetual instruments are supported";
                return message;
            }

            string baseAsset, quoteAsset;
            if (!SplitSymbol(parsed.Symbol, out baseAsset, out quoteAsset))
            {
                message.Error = "cannot split symbol into base/quote";
                return message;
            }

            var key = parsed.Symbol + "|" + message.Timeframe + "|" + limit;
            lock (_gate)
            {
                CacheEntry cached;
                if (_cache.TryGetValue(key, out cached) &&
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - cached.AtMs < 20_000)
                {
                    message.Points = cached.Points;
                    message.Period = cached.Period;
                    message.MinutePoints = cached.MinutePoints;
                    message.NextEndMs = cached.NextEndMs;
                    message.Live = cached.Live;
                    message.Available = cached.Available;
                    return message;
                }
            }

            string url;
            lock (_gate)
            {
                url = _baseUrl + "/api/instruments/" + Uri.EscapeDataString(baseAsset) + "/" +
                      Uri.EscapeDataString(quoteAsset) + "/futures/open-interest?timeframe=" +
                      Uri.EscapeDataString(message.Timeframe) + "&limit=" + limit.ToString(CultureInfo.InvariantCulture);
            }

            try
            {
                var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                ParseResponse(message, json);
                if (message.Available)
                {
                    lock (_gate)
                    {
                        if (_cache.Count > 128) _cache.Clear();
                        _cache[key] = new CacheEntry
                        {
                            AtMs = message.FetchedAtMs,
                            Points = message.Points,
                            Period = message.Period,
                            MinutePoints = message.MinutePoints,
                            NextEndMs = message.NextEndMs,
                            Live = message.Live,
                            Available = message.Available,
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                message.Error = ex.GetBaseException().Message;
                HubLog.Warn("screener OI history " + parsed.Symbol + ": " + message.Error);
            }

            return message;
        }

        public void Dispose()
        {
            _http.Dispose();
        }

        private static void ParseResponse(OiHistoryMessage message, string json)
        {
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                message.Available = Bool(root, "available");
                message.Period = String(root, "period");
                message.MinutePoints = Int(root, "minute_points");
                var next = Long(root, "next_end_ms");
                message.NextEndMs = next > 0 ? next : (long?)null;

                JsonElement points;
                if (root.TryGetProperty("points", out points) && points.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in points.EnumerateArray())
                    {
                        var t = Long(p, "t");
                        var oi = Double(p, "oi");
                        if (t > 0 && oi > 0) message.Points.Add(new OiHistoryPoint { T = t, Oi = oi });
                    }
                    message.Points.Sort((a, b) => a.T.CompareTo(b.T));
                }

                JsonElement live;
                if (root.TryGetProperty("live", out live) && live.ValueKind == JsonValueKind.Object)
                {
                    var oi = Double(live, "oi");
                    var ts = Long(live, "ts_ms");
                    if (oi > 0 && ts > 0)
                        message.Live = new OiHistoryLive { Oi = oi, TsMs = ts };
                }
            }
        }

        private static string NormalizeTimeframe(string timeframe)
        {
            var value = string.IsNullOrWhiteSpace(timeframe) ? "1m" : timeframe.Trim().ToLowerInvariant();
            var minutes = TimeframeMinutes(value);
            if (minutes >= 1440) return "1d";
            if (minutes >= 240) return "4h";
            if (minutes >= 60) return "1h";
            if (minutes >= 15) return "15m";
            if (minutes >= 5) return "5m";
            return "1m";
        }

        private static int TimeframeMinutes(string value)
        {
            if (value.Length < 2) return 1;
            int amount;
            if (!int.TryParse(value.Substring(0, value.Length - 1), out amount) || amount <= 0) return 1;
            switch (value[value.Length - 1])
            {
                case 'm': return amount;
                case 'h': return amount * 60;
                case 'd': return amount * 1440;
                case 'w': return amount * 7 * 1440;
                default: return 1;
            }
        }

        private static bool SplitSymbol(string symbol, out string baseAsset, out string quoteAsset)
        {
            baseAsset = "";
            quoteAsset = "";
            foreach (var quote in Quotes)
            {
                if (symbol.Length <= quote.Length || !symbol.EndsWith(quote, StringComparison.OrdinalIgnoreCase))
                    continue;
                baseAsset = symbol.Substring(0, symbol.Length - quote.Length).ToUpperInvariant();
                quoteAsset = quote;
                return true;
            }
            return false;
        }

        private static bool Bool(JsonElement element, string name)
        {
            JsonElement child;
            return element.TryGetProperty(name, out child) && child.ValueKind == JsonValueKind.True;
        }

        private static string String(JsonElement element, string name)
        {
            JsonElement child;
            return element.TryGetProperty(name, out child) && child.ValueKind == JsonValueKind.String
                ? child.GetString() : "";
        }

        private static long Long(JsonElement element, string name)
        {
            JsonElement child;
            long value;
            if (!element.TryGetProperty(name, out child)) return 0;
            if (child.ValueKind == JsonValueKind.Number && child.TryGetInt64(out value)) return value;
            return child.ValueKind == JsonValueKind.String &&
                   long.TryParse(child.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private static double Double(JsonElement element, string name)
        {
            JsonElement child;
            double value;
            if (!element.TryGetProperty(name, out child)) return 0;
            if (child.ValueKind == JsonValueKind.Number && child.TryGetDouble(out value)) return value;
            return child.ValueKind == JsonValueKind.String &&
                   double.TryParse(child.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private static int Int(JsonElement element, string name)
        {
            JsonElement child;
            int value;
            if (!element.TryGetProperty(name, out child)) return 0;
            if (child.ValueKind == JsonValueKind.Number && child.TryGetInt32(out value)) return value;
            return child.ValueKind == JsonValueKind.String &&
                   int.TryParse(child.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        private sealed class CacheEntry
        {
            public long AtMs;
            public List<OiHistoryPoint> Points;
            public string Period;
            public int MinutePoints;
            public long? NextEndMs;
            public OiHistoryLive Live;
            public bool Available;
        }
    }
}
