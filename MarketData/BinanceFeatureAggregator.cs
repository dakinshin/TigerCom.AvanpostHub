using System;
using System.Collections.Generic;
using System.Linq;

namespace AvanpostHub.MarketData
{
    /// <summary>
    /// Собирает Binance spot/perp events в один FeatureSnapshot на выбранном
    /// таймфрейме. Один экземпляр обслуживает одну пару instrument+timeframe.
    /// </summary>
    public sealed class BinanceFeatureAggregator
    {
        private readonly FeatureStore _store;
        private readonly string _instrument;
        private readonly string _timeframe;
        private readonly long _timeframeMs;
        private readonly bool _spotOnly;
        private readonly object _gate = new object();
        private readonly List<double> _spotImbalanceHistory = new List<double>();
        private readonly List<double> _perpImbalanceHistory = new List<double>();

        private BarState _bar;
        private double? _lastSpotPrice;
        private double? _lastPerpPrice;
        private double? _lastMarkPrice;
        private double? _lastIndexPrice;
        private double? _lastFundingRate;
        private double? _lastOi;
        private double? _lastOiUsd;
        private long _lastSpotEventMs;
        private long _lastPerpEventMs;
        private long _lastMarkEventMs;
        private long _lastOiEventMs;
        private long _lastLivePublishTick;

        public BinanceFeatureAggregator(
            FeatureStore store,
            string instrument,
            string timeframe,
            bool spotOnly = false)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _instrument = (instrument ?? "").Trim().ToUpperInvariant();
            _timeframe = string.IsNullOrWhiteSpace(timeframe) ? "1m" : timeframe.Trim().ToLowerInvariant();
            _timeframeMs = ParseTimeframeMs(_timeframe);
            _spotOnly = spotOnly;
        }

        public string Instrument => _instrument;
        public string Timeframe => _timeframe;

        public void OnSpotTrade(BinanceTrade trade)
        {
            if (trade.TradeTimeMs <= 0) return;
            lock (_gate)
            {
                if (!EnsureBar(trade.TradeTimeMs)) return;
                _lastSpotPrice = trade.Price;
                _lastSpotEventMs = trade.TradeTimeMs;
                UpdateHighLow(ref _bar.SpotHigh, ref _bar.SpotLow, trade.Price);
                if (!_bar.SpotOpen.HasValue) _bar.SpotOpen = trade.Price;
                _bar.SpotClose = trade.Price;
                if (trade.IsAggressiveBuy) _bar.SpotBuy += trade.Notional;
                else _bar.SpotSell += trade.Notional;
                PublishLive();
            }
        }

        public void OnPerpTrade(BinanceTrade trade)
        {
            if (_spotOnly || trade.TradeTimeMs <= 0) return;
            lock (_gate)
            {
                if (!EnsureBar(trade.TradeTimeMs)) return;
                _lastPerpPrice = trade.Price;
                _lastPerpEventMs = trade.TradeTimeMs;
                UpdateHighLow(ref _bar.PerpHigh, ref _bar.PerpLow, trade.Price);
                if (!_bar.PerpOpen.HasValue) _bar.PerpOpen = trade.Price;
                _bar.PerpClose = trade.Price;
                if (trade.IsAggressiveBuy) _bar.PerpBuy += trade.Notional;
                else _bar.PerpSell += trade.Notional;
                PublishLive();
            }
        }

        public void OnMarkPrice(BinanceMarkPrice mark)
        {
            if (_spotOnly || mark.EventTimeMs <= 0) return;
            lock (_gate)
            {
                if (!EnsureBar(mark.EventTimeMs)) return;
                _lastMarkPrice = mark.MarkPrice;
                _lastIndexPrice = mark.IndexPrice;
                _lastFundingRate = mark.FundingRate;
                _lastMarkEventMs = mark.EventTimeMs;
                _bar.MarkPrice = mark.MarkPrice;
                _bar.IndexPrice = mark.IndexPrice;
                _bar.FundingRate = mark.FundingRate;
                _bar.MarkEventMs = mark.EventTimeMs;
                PublishLive();
            }
        }

        public void OnOpenInterest(BinanceOpenInterest oi)
        {
            if (_spotOnly || oi.TimeMs <= 0 || oi.OpenInterest <= 0) return;
            lock (_gate)
            {
                if (!EnsureBar(oi.TimeMs)) return;
                _lastOi = oi.OpenInterest;
                _lastOiEventMs = oi.TimeMs;
                _bar.OiOpen = _bar.OiOpen ?? oi.OpenInterest;
                _bar.OiLast = oi.OpenInterest;
                _bar.OiEventMs = oi.TimeMs;
                _lastOiUsd = _lastPerpPrice.HasValue
                    ? oi.OpenInterest * _lastPerpPrice.Value
                    : _bar.OiLast * _bar.PerpClose;
                PublishLive();
            }
        }

        public void OnLiquidation(BinanceLiquidation liquidation)
        {
            if (_spotOnly || liquidation.TimeMs <= 0) return;
            lock (_gate)
            {
                if (!EnsureBar(liquidation.TimeMs)) return;
                if (liquidation.IsLong) _bar.LiqLongUsd += liquidation.Notional;
                else _bar.LiqShortUsd += liquidation.Notional;
                _bar.Liquidations++;
                PublishLive();
            }
        }

        /// <summary>Принудительно отдать live-снимок. Нужен тестам и диагностике.</summary>
        public void Flush()
        {
            lock (_gate)
            {
                if (_bar == null) return;
                Publish(false, _bar.LastEventMs);
            }
        }

        public static long ParseTimeframeMs(string timeframe)
        {
            if (string.IsNullOrWhiteSpace(timeframe)) return 60000;
            var value = timeframe.Trim().ToLowerInvariant();
            if (value.Length < 2) return 60000;

            int amount;
            if (!int.TryParse(value.Substring(0, value.Length - 1), out amount) || amount <= 0)
                return 60000;

            switch (value[value.Length - 1])
            {
                case 'm': return amount * 60_000L;
                case 'h': return amount * 3_600_000L;
                case 'd': return amount * 86_400_000L;
                case 'w': return amount * 7L * 86_400_000L;
                default: return 60000;
            }
        }

        private bool EnsureBar(long eventTimeMs)
        {
            if (_bar == null) _bar = NewBar(BarOpen(eventTimeMs));

            while (eventTimeMs >= _bar.OpenMs + _timeframeMs)
            {
                FinalizeBar(_bar.OpenMs + _timeframeMs - 1);
                _bar = NewBar(_bar.OpenMs + _timeframeMs);
            }

            if (eventTimeMs < _bar.OpenMs) return false;
            if (eventTimeMs > _bar.LastEventMs) _bar.LastEventMs = eventTimeMs;
            return true;
        }

        private BarState NewBar(long openMs)
        {
            return new BarState
            {
                OpenMs = openMs,
                LastEventMs = openMs,
                SpotOpen = _lastSpotPrice,
                SpotClose = _lastSpotPrice,
                PerpOpen = _lastPerpPrice,
                PerpClose = _lastPerpPrice,
                OiOpen = _lastOi,
                OiLast = _lastOi,
                MarkPrice = _lastMarkPrice,
                IndexPrice = _lastIndexPrice,
                FundingRate = _lastFundingRate,
            };
        }

        private void FinalizeBar(long asOfMs)
        {
            if (_bar == null) return;
            var snapshot = BuildSnapshot(true, asOfMs);
            _store.Publish(snapshot);
            AddImbalance(_spotImbalanceHistory, snapshot.Context.SpotDeltaShare);
            AddImbalance(_perpImbalanceHistory, snapshot.Context.PerpDeltaShare);
            _lastLivePublishTick = 0;
        }

        private void PublishLive()
        {
            if (_bar == null) return;
            var now = Environment.TickCount64;
            if (now - _lastLivePublishTick < 250) return;
            _lastLivePublishTick = now;
            Publish(false, _bar.LastEventMs);
        }

        private void Publish(bool final, long asOfMs)
        {
            if (_bar == null) return;
            var snapshot = BuildSnapshot(final, asOfMs);
            _store.Publish(snapshot);
        }

        private FeatureSnapshot BuildSnapshot(bool final, long asOfMs)
        {
            var spotVolume = _bar.SpotBuy + _bar.SpotSell;
            var perpVolume = _bar.PerpBuy + _bar.PerpSell;
            var spotDelta = spotVolume > 0 ? _bar.SpotBuy - _bar.SpotSell : (double?)null;
            var perpDelta = perpVolume > 0 ? _bar.PerpBuy - _bar.PerpSell : (double?)null;
            var spotImbalance = spotVolume > 0 ? spotDelta / spotVolume : (double?)null;
            var perpImbalance = perpVolume > 0 ? perpDelta / perpVolume : (double?)null;

            var spotPrice = _bar.SpotClose ?? _lastSpotPrice;
            var perpPrice = _bar.PerpClose ?? _lastPerpPrice;
            var spotZ = ZScore(spotImbalance, _spotImbalanceHistory);
            var perpZ = ZScore(perpImbalance, _perpImbalanceHistory);
            var basis = spotPrice.HasValue && perpPrice.HasValue && spotPrice.Value > 0
                ? (perpPrice.Value / spotPrice.Value - 1.0) * 10000.0
                : (double?)null;

            var oi = _bar.OiLast ?? _lastOi;
            var oiOpen = _bar.OiOpen ?? _lastOi;
            var oiChange = oi.HasValue && oiOpen.HasValue && oiOpen.Value > 0
                ? (oi.Value / oiOpen.Value - 1.0) * 100.0
                : (double?)null;
            var oiUsd = oi.HasValue && perpPrice.HasValue
                ? oi.Value * perpPrice.Value
                : _lastOiUsd;
            var fundingRate = _bar.FundingRate ?? _lastFundingRate;
            var liqTotal = _bar.LiqLongUsd + _bar.LiqShortUsd;
            var liqImbalance = liqTotal > 0
                ? (_bar.LiqShortUsd - _bar.LiqLongUsd) / liqTotal
                : (double?)null;
            var liqIntensity = oiUsd.HasValue && oiUsd.Value > 0
                ? liqTotal / oiUsd.Value
                : (double?)null;

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var spotAge = _lastSpotEventMs > 0 ? Math.Max(0, nowMs - _lastSpotEventMs) : (long?)null;
            var oiAge = _lastOiEventMs > 0 ? Math.Max(0, nowMs - _lastOiEventMs) : (long?)null;
            var fundingAge = _lastMarkEventMs > 0 ? Math.Max(0, nowMs - _lastMarkEventMs) : (long?)null;
            var stale = (spotAge.HasValue && spotAge.Value > 5000) ||
                        (oiAge.HasValue && oiAge.Value > 15000) ||
                        (fundingAge.HasValue && fundingAge.Value > 15000);

            return new FeatureSnapshot
            {
                Instrument = _instrument,
                SpotInstrument = _spotOnly ? _instrument : "BINANCE:" + SymbolFromInstrument() + ":SPOT",
                Timeframe = _timeframe,
                BarOpenMs = _bar.OpenMs,
                AsOfMs = asOfMs > 0 ? asOfMs : _bar.LastEventMs,
                Final = final,
                Context = new ContextFeatures
                {
                    SpotCvd = spotDelta,
                    PerpCvd = perpDelta,
                    SpotCvdZ = spotZ,
                    PerpCvdZ = perpZ,
                    SpotPerpDiv = spotImbalance.HasValue && perpImbalance.HasValue
                        ? spotImbalance - perpImbalance
                        : (double?)null,
                    SpotShare = spotVolume + perpVolume > 0
                        ? spotVolume / (spotVolume + perpVolume)
                        : (double?)null,
                    SpotDeltaShare = spotImbalance,
                    PerpDeltaShare = perpImbalance,
                    SpotVolume = spotVolume,
                    PerpVolume = perpVolume,
                    SpotPrice = spotPrice,
                    PerpPrice = perpPrice,
                    BasisBps = basis,
                    FundingRate = fundingRate,
                    FundingRatePct = fundingRate.HasValue ? fundingRate * 100.0 : (double?)null,
                    OiBase = oi,
                    OiUsd = oiUsd,
                    OiChangePct = oiChange,
                    GlobalOiDeltaPct = oiChange,
                    MarkPrice = _bar.MarkPrice ?? _lastMarkPrice,
                    IndexPrice = _bar.IndexPrice ?? _lastIndexPrice,
                    LiqLongUsd = _bar.LiqLongUsd,
                    LiqShortUsd = _bar.LiqShortUsd,
                    LiqImbalance = liqImbalance,
                    LiqIntensity = liqIntensity,
                },
                Quality = new QualityFeatures
                {
                    Stale = stale,
                    FreshVenues = oi.HasValue ? 1 : 0,
                    SpotAgeMs = spotAge,
                    OiAgeMs = oiAge,
                    FundingAgeMs = fundingAge,
                    PartialLiquidations = true,
                    Source = "BINANCE",
                },
            };
        }

        private string SymbolFromInstrument()
        {
            var parts = _instrument.Split(':');
            return parts.Length > 1 ? parts[1] : _instrument;
        }

        private static void UpdateHighLow(ref double? high, ref double? low, double price)
        {
            if (!high.HasValue || price > high.Value) high = price;
            if (!low.HasValue || price < low.Value) low = price;
        }

        private static void AddImbalance(List<double> history, double? value)
        {
            if (!value.HasValue) return;
            history.Add(value.Value);
            if (history.Count > 500) history.RemoveRange(0, history.Count - 500);
        }

        private static double? ZScore(double? value, List<double> history)
        {
            if (!value.HasValue || history == null || history.Count < 20) return null;
            var mean = history.Average();
            var variance = history.Sum(x => (x - mean) * (x - mean)) / history.Count;
            var std = Math.Sqrt(variance);
            return std > 1e-9 ? (value.Value - mean) / std : (double?)null;
        }

        private long BarOpen(long eventTimeMs)
        {
            return eventTimeMs - (eventTimeMs % _timeframeMs);
        }

        private sealed class BarState
        {
            public long OpenMs;
            public long LastEventMs;
            public double? SpotOpen;
            public double? SpotHigh;
            public double? SpotLow;
            public double? SpotClose;
            public double? PerpOpen;
            public double? PerpHigh;
            public double? PerpLow;
            public double? PerpClose;
            public double SpotBuy;
            public double SpotSell;
            public double PerpBuy;
            public double PerpSell;
            public double LiqLongUsd;
            public double LiqShortUsd;
            public long Liquidations;
            public double? OiOpen;
            public double? OiLast;
            public long OiEventMs;
            public double? MarkPrice;
            public double? IndexPrice;
            public double? FundingRate;
            public long MarkEventMs;
        }
    }
}
