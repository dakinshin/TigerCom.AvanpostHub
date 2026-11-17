using System;

namespace AvanpostHub.MarketData
{
    public struct BinanceTrade
    {
        public string Symbol;
        public long EventTimeMs;
        public long TradeTimeMs;
        public double Price;
        public double Quantity;
        public bool BuyerIsMaker;
        public bool IsSpot;

        public double Notional => Price * Quantity;
        public bool IsAggressiveBuy => !BuyerIsMaker;
    }

    public struct BinanceMarkPrice
    {
        public string Symbol;
        public long EventTimeMs;
        public long NextFundingTimeMs;
        public double MarkPrice;
        public double IndexPrice;
        public double FundingRate;
    }

    public struct BinanceOpenInterest
    {
        public string Symbol;
        public long TimeMs;
        public double OpenInterest;
    }

    public struct BinanceLiquidation
    {
        public string Symbol;
        public long TimeMs;
        public bool IsLong;
        public double Price;
        public double Quantity;

        public double Notional => Price * Quantity;
    }

    public enum BinanceMarketType
    {
        Spot,
        Perpetual,
    }

    public sealed class BinanceInstrument
    {
        public string Instrument { get; private set; }
        public string Symbol { get; private set; }
        public BinanceMarketType Market { get; private set; }

        public static bool TryParse(string value, out BinanceInstrument instrument)
        {
            instrument = null;
            if (string.IsNullOrWhiteSpace(value)) return false;

            var parts = value.Trim().Split(':');
            if (parts.Length < 3) return false;
            if (!string.Equals(parts[0], "BINANCE", StringComparison.OrdinalIgnoreCase)) return false;

            var symbol = (parts[1] ?? "").Trim().ToUpperInvariant();
            var market = (parts[2] ?? "").Trim().ToUpperInvariant();
            if (symbol.Length == 0) return false;

            BinanceMarketType type;
            if (market == "SPOT") type = BinanceMarketType.Spot;
            else if (market == "PERP" || market == "PERPETUAL" || market == "FUTURE" || market == "FUTURES")
                type = BinanceMarketType.Perpetual;
            else return false;

            instrument = new BinanceInstrument
            {
                Symbol = symbol,
                Market = type,
                Instrument = "BINANCE:" + symbol + ":" + (type == BinanceMarketType.Spot ? "SPOT" : "PERP"),
            };
            return true;
        }

        public override string ToString() => Instrument;
    }
}
