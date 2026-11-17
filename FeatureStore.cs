//--------------------------------------------------------------------------------
// FeatureStore — общий кэш нормализованных признаков.
//
//  • live-снимки обновляют Latest и сразу уходят подписчикам;
//  • final-снимки дополнительно попадают в историю по бару;
//  • повторный final за тот же barOpenMs заменяет предыдущую версию;
//  • наружу отдаются копии, чтобы потребители не могли испортить кэш.
//--------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;

namespace AvanpostHub
{
    public sealed class FeatureStore
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, Series> _series =
            new Dictionary<string, Series>(StringComparer.OrdinalIgnoreCase);
        private readonly int _maxHistoryPerSeries;

        public FeatureStore(int maxHistoryPerSeries = 5000)
        {
            _maxHistoryPerSeries = Math.Max(1, maxHistoryPerSeries);
        }

        public event Action<FeatureSnapshot> SnapshotPublished;

        public int SeriesCount
        {
            get { lock (_gate) return _series.Count; }
        }

        public int HistoryCount
        {
            get
            {
                lock (_gate)
                    return _series.Values.Sum(x => x.History.Count);
            }
        }

        public static string Key(string instrument, string timeframe)
        {
            return NormalizeInstrument(instrument) + "|" + NormalizeTimeframe(timeframe);
        }

        public void Publish(FeatureSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var stored = snapshot.Clone();
            stored.Instrument = NormalizeInstrument(stored.Instrument);
            if (stored.Instrument.Length == 0)
                throw new ArgumentException("Instrument is required.", nameof(snapshot));
            stored.Timeframe = NormalizeTimeframe(stored.Timeframe);
            if (stored.Timeframe.Length == 0) stored.Timeframe = "1m";
            if (stored.AsOfMs <= 0) stored.AsOfMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (stored.BarOpenMs <= 0) stored.BarOpenMs = stored.AsOfMs;

            var key = Key(stored.Instrument, stored.Timeframe);
            lock (_gate)
            {
                Series series;
                if (!_series.TryGetValue(key, out series))
                {
                    series = new Series();
                    _series[key] = series;
                }

                if (series.Latest == null ||
                    stored.BarOpenMs > series.Latest.BarOpenMs ||
                    (stored.BarOpenMs == series.Latest.BarOpenMs &&
                     (stored.Final || stored.AsOfMs >= series.Latest.AsOfMs)))
                    series.Latest = stored;

                if (stored.Final) PutFinal(series, stored);
            }

            var handler = SnapshotPublished;
            if (handler == null) return;
            try { handler(stored.Clone()); }
            catch (Exception ex) { HubLog.Warn("feature subscriber failed: " + ex.Message); }
        }

        public FeatureSnapshot GetLatest(string instrument, string timeframe)
        {
            lock (_gate)
            {
                Series series;
                if (!_series.TryGetValue(Key(instrument, timeframe), out series) || series.Latest == null)
                    return null;
                return series.Latest.Clone();
            }
        }

        public IReadOnlyList<FeatureSnapshot> GetHistory(
            string instrument,
            string timeframe,
            long fromMs = 0,
            long toMs = long.MaxValue,
            int limit = 500)
        {
            limit = Math.Max(1, Math.Min(limit, 5000));
            lock (_gate)
            {
                Series series;
                if (!_series.TryGetValue(Key(instrument, timeframe), out series))
                    return new FeatureSnapshot[0];

                var rows = series.History
                    .Where(x => x.BarOpenMs >= fromMs && x.BarOpenMs <= toMs)
                    .OrderBy(x => x.BarOpenMs)
                    .ToList();

                if (rows.Count > limit)
                    rows = rows.Skip(rows.Count - limit).ToList();

                return rows.Select(x => x.Clone()).ToArray();
            }
        }

        private void PutFinal(Series series, FeatureSnapshot snapshot)
        {
            var index = series.History.FindIndex(x => x.BarOpenMs == snapshot.BarOpenMs);
            if (index >= 0)
            {
                series.History[index] = snapshot;
                return;
            }

            series.History.Add(snapshot);
            if (series.History.Count <= _maxHistoryPerSeries) return;

            var remove = series.History.Count - _maxHistoryPerSeries;
            series.History.RemoveRange(0, remove);
        }

        private static string NormalizeInstrument(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant();
        }

        private static string NormalizeTimeframe(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "1m" : value.Trim().ToLowerInvariant();
        }

        private sealed class Series
        {
            public FeatureSnapshot Latest;
            public readonly List<FeatureSnapshot> History = new List<FeatureSnapshot>();
        }
    }
}
