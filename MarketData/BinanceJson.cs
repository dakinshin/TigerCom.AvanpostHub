using System;
using System.Globalization;
using System.Text.Json;

namespace AvanpostHub.MarketData
{
    public static class BinanceJson
    {
        public static bool TryParseTrade(string json, out BinanceTrade trade)
        {
            trade = default(BinanceTrade);
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    string eventType;
                    if (!TryString(root, "e", out eventType) || eventType != "aggTrade") return false;

                    string symbol, price, quantity, maker;
                    long eventMs, tradeMs;
                    if (!TryString(root, "s", out symbol) ||
                        !TryString(root, "p", out price) ||
                        !TryString(root, "q", out quantity) ||
                        !TryString(root, "m", out maker) ||
                        !TryLong(root, "E", out eventMs) ||
                        !TryLong(root, "T", out tradeMs))
                        return false;

                    trade = new BinanceTrade
                    {
                        Symbol = symbol.ToUpperInvariant(),
                        EventTimeMs = eventMs,
                        TradeTimeMs = tradeMs,
                        Price = ParseDouble(price),
                        Quantity = ParseDouble(quantity),
                        BuyerIsMaker = string.Equals(maker, "true", StringComparison.OrdinalIgnoreCase),
                    };
                    return trade.Price > 0 && trade.Quantity > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool TryParseMarkPrice(string json, out BinanceMarkPrice mark)
        {
            mark = default(BinanceMarkPrice);
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    string eventType;
                    if (!TryString(root, "e", out eventType) || eventType != "markPriceUpdate") return false;

                    string symbol, price, index, funding;
                    long eventMs;
                    long nextFundingMs;
                    if (!TryString(root, "s", out symbol) ||
                        !TryString(root, "p", out price) ||
                        !TryLong(root, "E", out eventMs) ||
                        !TryLong(root, "T", out nextFundingMs))
                        return false;

                    TryString(root, "i", out index);
                    TryString(root, "r", out funding);

                    mark = new BinanceMarkPrice
                    {
                        Symbol = symbol.ToUpperInvariant(),
                        EventTimeMs = eventMs,
                        NextFundingTimeMs = nextFundingMs,
                        MarkPrice = ParseDouble(price),
                        IndexPrice = ParseDouble(index),
                        FundingRate = ParseDouble(funding),
                    };
                    return mark.MarkPrice > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool TryParseOpenInterest(string json, out BinanceOpenInterest oi)
        {
            oi = default(BinanceOpenInterest);
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    string amount;
                    long timeMs;
                    if (!TryString(root, "openInterest", out amount) || !TryLong(root, "time", out timeMs))
                        return false;

                    string symbol = "";
                    TryString(root, "symbol", out symbol);
                    oi = new BinanceOpenInterest
                    {
                        Symbol = (symbol ?? "").ToUpperInvariant(),
                        TimeMs = timeMs,
                        OpenInterest = ParseDouble(amount),
                    };
                    return oi.OpenInterest > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool TryParseLiquidation(string json, out BinanceLiquidation liquidation)
        {
            liquidation = default(BinanceLiquidation);
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    string eventType;
                    if (!TryString(root, "e", out eventType) || eventType != "forceOrder") return false;

                    JsonElement order;
                    if (!root.TryGetProperty("o", out order) || order.ValueKind != JsonValueKind.Object) return false;

                    string symbol, side, quantity, averagePrice, orderPrice, time;
                    if (!TryString(order, "s", out symbol) ||
                        !TryString(order, "S", out side) ||
                        !TryString(order, "q", out quantity))
                        return false;

                    TryString(order, "ap", out averagePrice);
                    TryString(order, "p", out orderPrice);
                    TryString(order, "T", out time);

                    long timeMs;
                    if (!long.TryParse(time, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeMs))
                    {
                        string eventMs;
                        if (!TryString(root, "E", out eventMs) ||
                            !long.TryParse(eventMs, NumberStyles.Integer, CultureInfo.InvariantCulture, out timeMs))
                            return false;
                    }

                    var price = ParseDouble(averagePrice);
                    if (price <= 0) price = ParseDouble(orderPrice);

                    liquidation = new BinanceLiquidation
                    {
                        Symbol = symbol.ToUpperInvariant(),
                        TimeMs = timeMs,
                        IsLong = string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase),
                        Price = price,
                        Quantity = ParseDouble(quantity),
                    };
                    return liquidation.Price > 0 && liquidation.Quantity > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryString(JsonElement element, string name, out string value)
        {
            value = null;
            JsonElement child;
            if (!element.TryGetProperty(name, out child)) return false;

            if (child.ValueKind == JsonValueKind.String)
            {
                value = child.GetString();
                return value != null;
            }

            if (child.ValueKind == JsonValueKind.Number || child.ValueKind == JsonValueKind.True ||
                child.ValueKind == JsonValueKind.False)
            {
                value = child.GetRawText();
                return true;
            }

            return false;
        }

        private static bool TryLong(JsonElement element, string name, out long value)
        {
            value = 0;
            string text;
            if (!TryString(element, name, out text)) return false;
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static double ParseDouble(string value)
        {
            double parsed;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : 0;
        }
    }
}
