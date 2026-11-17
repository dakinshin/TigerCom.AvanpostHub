using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AvanpostHub;

internal static class Program
{
    private static int _failed;

    private static int Main()
    {
        TestFinalHistory();
        TestLiveDoesNotCreateHistory();
        TestHistoryLimitAndFilter();
        TestCloneIsolation();
        TestJsonContract();
        TestRequestContract();
        TestSettingsSanitize();
        TestSettingsStore();
        TestLoopbackServer();

        Console.WriteLine(_failed == 0 ? "AvanpostHub tests: OK" : "AvanpostHub tests: FAILED " + _failed);
        return _failed == 0 ? 0 : 1;
    }

    private static void TestFinalHistory()
    {
        var store = new FeatureStore();
        store.Publish(Snapshot(1000, 0.1, true));
        Check(store.SeriesCount == 1, "final создаёт серию");
        Check(store.HistoryCount == 1, "final попадает в историю");
        Check(store.GetLatest("BINANCE:BTCUSDT:PERP", "1m").Context.GlobalOiDeltaPct == 0.1,
            "latest обновляется");

        store.Publish(Snapshot(1000, 0.2, true));
        var history = store.GetHistory("BINANCE:BTCUSDT:PERP", "1m");
        Check(history.Count == 1 && history[0].Context.GlobalOiDeltaPct == 0.2,
            "повторный final за тот же бар заменяет запись");
    }

    private static void TestLiveDoesNotCreateHistory()
    {
        var store = new FeatureStore();
        store.Publish(Snapshot(1000, 0.1, true));
        store.Publish(Snapshot(2000, 0.2, false));
        Check(store.HistoryCount == 1, "live не попадает в историю");
        Check(store.GetLatest("BINANCE:BTCUSDT:PERP", "1m").BarOpenMs == 2000,
            "live обновляет latest");

        store.Publish(Snapshot(500, 0.3, true));
        Check(store.GetLatest("BINANCE:BTCUSDT:PERP", "1m").BarOpenMs == 2000,
            "поздний final старого бара не откатывает latest");
    }

    private static void TestHistoryLimitAndFilter()
    {
        var store = new FeatureStore(2);
        store.Publish(Snapshot(1000, 1, true));
        store.Publish(Snapshot(2000, 2, true));
        store.Publish(Snapshot(3000, 3, true));
        var all = store.GetHistory("BINANCE:BTCUSDT:PERP", "1m");
        Check(all.Count == 2 && all[0].BarOpenMs == 2000 && all[1].BarOpenMs == 3000,
            "история ограничивается maxHistoryPerSeries");

        var filtered = store.GetHistory("BINANCE:BTCUSDT:PERP", "1m", 2500, 3500, 10);
        Check(filtered.Count == 1 && filtered[0].BarOpenMs == 3000,
            "history фильтрует по from/to");

        var limited = store.GetHistory("BINANCE:BTCUSDT:PERP", "1m", 0, long.MaxValue, 1);
        Check(limited.Count == 1 && limited[0].BarOpenMs == 3000,
            "history возвращает последние limit баров");
    }

    private static void TestCloneIsolation()
    {
        var store = new FeatureStore();
        var source = Snapshot(1000, 0.1, true);
        store.Publish(source);
        source.Context.GlobalOiDeltaPct = 99;
        var stored = store.GetLatest("BINANCE:BTCUSDT:PERP", "1m");
        Check(stored.Context.GlobalOiDeltaPct == 0.1, "Publish сохраняет копию");

        stored.Context.GlobalOiDeltaPct = 42;
        var again = store.GetLatest("BINANCE:BTCUSDT:PERP", "1m");
        Check(again.Context.GlobalOiDeltaPct == 0.1, "GetLatest отдаёт копию");
    }

    private static void TestJsonContract()
    {
        var json = HubJson.Serialize(Snapshot(1000, 0.25, true));
        Check(json.Contains("\"asOfMs\":", StringComparison.Ordinal), "JSON использует camelCase");
        Check(json.Contains("\"globalOiDeltaPct\":", StringComparison.Ordinal), "вложенный JSON использует camelCase");
        Check(!json.Contains("\"AsOfMs\":", StringComparison.Ordinal), "JSON не содержит PascalCase");
    }

    private static void TestRequestContract()
    {
        var req = HubJson.Deserialize<HubRequest>(
            "{\"type\":\"subscribe\",\"instrument\":\"Binance:BTCUSDT:Perp\",\"timeframe\":\"1m\"}");
        Check(req != null && req.Type == "subscribe", "HubRequest разбирается");
        Check(req.Instrument == "Binance:BTCUSDT:Perp", "instrument не теряется");
    }

    private static void TestSettingsSanitize()
    {
        var settings = new HubSettings { Port = 0, Autostart = true, StartMinimized = false }.Sanitized();
        Check(settings.Port == 7830, "некорректный порт заменяется на 7830");
        Check(settings.Autostart, "autostart сохраняется");
        Check(!settings.StartMinimized, "startMinimized сохраняется");
    }

    private static void TestSettingsStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "avanposthub-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SettingsStore(path);
            store.Save(new HubSettings { Port = 17830, Autostart = true, StartMinimized = false });
            var loaded = store.Load();
            Check(loaded.Port == 17830 && loaded.Autostart && !loaded.StartMinimized, "settings сохраняются и читаются");
        }
        finally
        {
            try { File.Delete(path); } catch { }
            try { File.Delete(path + ".tmp"); } catch { }
        }
    }

    private static void TestLoopbackServer()
    {
        var port = FindFreePort();
        var store = new FeatureStore();
        using (var server = new LoopbackServer(store))
        {
            server.Start(port);
            using (var http = new HttpClient())
            {
                var health = http.GetStringAsync("http://127.0.0.1:" + port + "/health").GetAwaiter().GetResult();
                Check(health.Contains("\"status\":\"ok\"", StringComparison.Ordinal), "HTTP /health отвечает");

                var status = http.GetStringAsync("http://127.0.0.1:" + port + "/v1/status").GetAwaiter().GetResult();
                Check(status.Contains("\"state\":\"running\"", StringComparison.Ordinal), "HTTP /v1/status отдаёт running");
            }

            var ws = new ClientWebSocket();
            try
            {
                ws.ConnectAsync(new Uri("ws://127.0.0.1:" + port + "/ws"), CancellationToken.None).GetAwaiter().GetResult();
                Check(ReceiveText(ws).Contains("\"type\":\"hello\"", StringComparison.Ordinal), "WS hello");
                Check(ReceiveText(ws).Contains("\"type\":\"status\"", StringComparison.Ordinal), "WS status");

                var subscribe = "{\"type\":\"subscribe\",\"instrument\":\"BINANCE:BTCUSDT:PERP\",\"timeframe\":\"1m\"}";
                SendText(ws, subscribe);
                Check(ReceiveText(ws).Contains("\"type\":\"subscribed\"", StringComparison.Ordinal), "WS subscribe");

                store.Publish(Snapshot(5000, 0.5, true));
                var features = ReceiveText(ws);
                Check(features.Contains("\"type\":\"features\"", StringComparison.Ordinal) &&
                      features.Contains("\"globalOiDeltaPct\":0.5", StringComparison.Ordinal), "WS broadcast features");

                SendText(ws, "{\"type\":\"ping\"}");
                Check(ReceiveText(ws).Contains("\"type\":\"pong\"", StringComparison.Ordinal), "WS ping/pong");

                SendText(ws, "{\"type\":\"history\",\"instrument\":\"BINANCE:BTCUSDT:PERP\",\"timeframe\":\"1m\",\"limit\":10}");
                Check(ReceiveText(ws).Contains("\"type\":\"history\"", StringComparison.Ordinal), "WS history");
            }
            finally
            {
                ws.Dispose();
            }
        }
    }

    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void SendText(ClientWebSocket ws, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
            .GetAwaiter().GetResult();
    }

    private static string ReceiveText(ClientWebSocket ws)
    {
        var buffer = new byte[4096];
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        using (var output = new System.IO.MemoryStream())
        {
            while (true)
            {
                var result = ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token).GetAwaiter().GetResult();
                if (result.MessageType == WebSocketMessageType.Close) return "";
                output.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) return Encoding.UTF8.GetString(output.ToArray());
            }
        }
    }

    private static FeatureSnapshot Snapshot(long bar, double delta, bool final)
    {
        return new FeatureSnapshot
        {
            Instrument = "BINANCE:BTCUSDT:PERP",
            Timeframe = "1m",
            BarOpenMs = bar,
            AsOfMs = bar + 59999,
            Final = final,
            Context = new ContextFeatures { GlobalOiDeltaPct = delta },
            Quality = new QualityFeatures { Source = "test", FreshVenues = 3 },
        };
    }

    private static void Check(bool condition, string message)
    {
        if (condition)
        {
            Console.WriteLine("ok  " + message);
            return;
        }

        _failed++;
        Console.WriteLine("FAIL " + message);
    }
}
