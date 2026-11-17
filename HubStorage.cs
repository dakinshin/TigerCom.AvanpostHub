//--------------------------------------------------------------------------------
// HubStorage — простое durable-хранилище без внешних зависимостей.
//
// Формат — append-only JSONL по сериям:
//   %LOCALAPPDATA%\AvanpostHub\data\oi\<ключ>.jsonl
//   %LOCALAPPDATA%\AvanpostHub\data\features\<ключ>.jsonl
//
// Запись идёт под lock, чтение строит словарь по bar time и оставляет
// последнюю версию. Битые строки пропускаются: файл не должен ронять Hub.
//--------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AvanpostHub
{
    public sealed class StoredOiPoint
    {
        public long T { get; set; }
        public double Oi { get; set; }
        public string Source { get; set; } = "";
        public int GranularityMin { get; set; }
    }

    public sealed class HubStorage : IDisposable
    {
        private readonly object _gate = new object();
        private readonly string _oiDir;
        private readonly string _featuresDir;
        private readonly Dictionary<string, Dictionary<long, StoredOiPoint>> _oi =
            new Dictionary<string, Dictionary<long, StoredOiPoint>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<long, FeatureSnapshot>> _features =
            new Dictionary<string, Dictionary<long, FeatureSnapshot>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _loadedOi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _loadedFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const int CompactAfter = 20_000;

        public HubStorage(string root = null)
        {
            var dir = string.IsNullOrWhiteSpace(root)
                ? Path.Combine(AppPaths.Root, "data")
                : root.Trim();
            _oiDir = Path.Combine(dir, "oi");
            _featuresDir = Path.Combine(dir, "features");
            Directory.CreateDirectory(_oiDir);
            Directory.CreateDirectory(_featuresDir);
        }

        public int LoadedOiPoints
        {
            get { lock (_gate) return _oi.Values.Sum(x => x.Count); }
        }

        public int LoadedFeatureBars
        {
            get { lock (_gate) return _features.Values.Sum(x => x.Count); }
        }

        public IReadOnlyList<StoredOiPoint> LoadOi(
            string instrument,
            string timeframe,
            long fromMs = 0,
            long toMs = long.MaxValue,
            int limit = 500)
        {
            var key = SeriesKey(instrument, timeframe);
            lock (_gate)
            {
                var series = LoadOiLocked(key);
                var rows = series.Values
                    .Where(x => x.T >= fromMs && x.T <= toMs)
                    .OrderBy(x => x.T)
                    .ToList();
                return rows.Skip(Math.Max(0, rows.Count - Math.Max(1, limit)))
                    .Take(Math.Max(1, limit))
                    .Select(Clone)
                    .ToArray();
            }
        }

        public void AppendOi(string instrument, string timeframe, IEnumerable<StoredOiPoint> points)
        {
            if (points == null) return;
            var key = SeriesKey(instrument, timeframe);
            lock (_gate)
            {
                var series = LoadOiLocked(key);
                var file = OiPath(key);
                var added = new List<StoredOiPoint>();
                foreach (var point in points)
                {
                    if (point == null || point.T <= 0 || point.Oi <= 0) continue;
                    StoredOiPoint old;
                    if (series.TryGetValue(point.T, out old) &&
                        Math.Abs(old.Oi - point.Oi) < 1e-12 &&
                        string.Equals(old.Source, point.Source, StringComparison.OrdinalIgnoreCase)) continue;

                    var stored = Clone(point);
                    series[point.T] = stored;
                    added.Add(stored);
                }

                if (added.Count == 0) return;
                AppendLines(file, added.Select(x => JsonSerializer.Serialize(x, HubJson.Options)));
                if (series.Count > CompactAfter) CompactOiLocked(key, series);
            }
        }

        public void AppendFeature(FeatureSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.Final) return;
            var key = SeriesKey(snapshot.Instrument, snapshot.Timeframe);
            lock (_gate)
            {
                var series = LoadFeaturesLocked(key);
                var copy = snapshot.Clone();
                series[copy.BarOpenMs] = copy;

                var file = FeaturePath(key);
                AppendLines(file, new[] { JsonSerializer.Serialize(copy, HubJson.Options) });
                if (series.Count > CompactAfter) CompactFeaturesLocked(key, series);
            }
        }

        public IReadOnlyList<FeatureSnapshot> LoadFeatures(
            string instrument,
            string timeframe,
            long fromMs = 0,
            long toMs = long.MaxValue,
            int limit = 500)
        {
            var key = SeriesKey(instrument, timeframe);
            lock (_gate)
            {
                var series = LoadFeaturesLocked(key);
                var rows = series.Values
                    .Where(x => x.BarOpenMs >= fromMs && x.BarOpenMs <= toMs)
                    .OrderBy(x => x.BarOpenMs)
                    .ToList();
                return rows.Skip(Math.Max(0, rows.Count - Math.Max(1, limit)))
                    .Take(Math.Max(1, limit))
                    .Select(x => x.Clone())
                    .ToArray();
            }
        }

        public void Dispose()
        {
        }

        private Dictionary<long, StoredOiPoint> LoadOiLocked(string key)
        {
            Dictionary<long, StoredOiPoint> series;
            if (_oi.TryGetValue(key, out series)) return series;
            series = new Dictionary<long, StoredOiPoint>();
            if (_loadedOi.Add(key))
            {
                foreach (var line in ReadLines(OiPath(key)))
                {
                    try
                    {
                        var point = JsonSerializer.Deserialize<StoredOiPoint>(line, HubJson.Options);
                        if (point != null && point.T > 0 && point.Oi > 0) series[point.T] = point;
                    }
                    catch { }
                }
            }
            _oi[key] = series;
            return series;
        }

        private Dictionary<long, FeatureSnapshot> LoadFeaturesLocked(string key)
        {
            Dictionary<long, FeatureSnapshot> series;
            if (_features.TryGetValue(key, out series)) return series;
            series = new Dictionary<long, FeatureSnapshot>();
            if (_loadedFeatures.Add(key))
            {
                foreach (var line in ReadLines(FeaturePath(key)))
                {
                    try
                    {
                        var snapshot = JsonSerializer.Deserialize<FeatureSnapshot>(line, HubJson.Options);
                        if (snapshot != null && snapshot.BarOpenMs > 0) series[snapshot.BarOpenMs] = snapshot;
                    }
                    catch { }
                }
            }
            _features[key] = series;
            return series;
        }

        private void CompactOiLocked(string key, Dictionary<long, StoredOiPoint> series)
        {
            var tmp = OiPath(key) + ".tmp";
            File.WriteAllLines(tmp, series.Values.OrderBy(x => x.T)
                .Select(x => JsonSerializer.Serialize(x, HubJson.Options)));
            File.Move(tmp, OiPath(key), true);
        }

        private void CompactFeaturesLocked(string key, Dictionary<long, FeatureSnapshot> series)
        {
            var tmp = FeaturePath(key) + ".tmp";
            File.WriteAllLines(tmp, series.Values.OrderBy(x => x.BarOpenMs)
                .Select(x => JsonSerializer.Serialize(x, HubJson.Options)));
            File.Move(tmp, FeaturePath(key), true);
        }

        private static IEnumerable<string> ReadLines(string path)
        {
            if (!File.Exists(path)) yield break;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                    if (!string.IsNullOrWhiteSpace(line)) yield return line;
            }
        }

        private static void AppendLines(string path, IEnumerable<string> lines)
        {
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                foreach (var line in lines) writer.WriteLine(line);
            }
        }

        private string OiPath(string key) => Path.Combine(_oiDir, Safe(key) + ".jsonl");
        private string FeaturePath(string key) => Path.Combine(_featuresDir, Safe(key) + ".jsonl");

        private static string SeriesKey(string instrument, string timeframe)
        {
            var i = string.IsNullOrWhiteSpace(instrument) ? "" : instrument.Trim().ToUpperInvariant();
            var t = string.IsNullOrWhiteSpace(timeframe) ? "1m" : timeframe.Trim().ToLowerInvariant();
            return i + "|" + t;
        }

        private static string Safe(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            return sb.ToString();
        }

        private static StoredOiPoint Clone(StoredOiPoint point) =>
            new StoredOiPoint { T = point.T, Oi = point.Oi, Source = point.Source, GranularityMin = point.GranularityMin };
    }
}
