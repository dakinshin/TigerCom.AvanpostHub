//--------------------------------------------------------------------------------
// Contracts — wire-модель Avanpost Hub.
//
// Hub отдаёт не сырые биржевые события, а нормализованные признаки.
// Все времена — unix milliseconds UTC. Наличие asOf/barOpen/final позволяет
// потребителю не использовать будущие данные и отличать живой бар от закрытого.
//--------------------------------------------------------------------------------

using System;

namespace AvanpostHub
{
    public sealed class FeatureSnapshot
    {
        public string Type { get; set; } = "features";
        public string Instrument { get; set; } = "";
        public string SpotInstrument { get; set; }
        public string Timeframe { get; set; } = "1m";
        public long BarOpenMs { get; set; }
        public long AsOfMs { get; set; }
        public bool Final { get; set; }
        public ContextFeatures Context { get; set; } = new ContextFeatures();
        public SlowContext Slow { get; set; } = new SlowContext();
        public QualityFeatures Quality { get; set; } = new QualityFeatures();

        public FeatureSnapshot Clone()
        {
            return new FeatureSnapshot
            {
                Type = Type,
                Instrument = Instrument,
                SpotInstrument = SpotInstrument,
                Timeframe = Timeframe,
                BarOpenMs = BarOpenMs,
                AsOfMs = AsOfMs,
                Final = Final,
                Context = Context != null ? Context.Clone() : new ContextFeatures(),
                Slow = Slow != null ? Slow.Clone() : new SlowContext(),
                Quality = Quality != null ? Quality.Clone() : new QualityFeatures(),
            };
        }
    }

    public sealed class ContextFeatures
    {
        // Спот и перп.
        public double? SpotCvd { get; set; }
        public double? PerpCvd { get; set; }
        public double? SpotCvdZ { get; set; }
        public double? PerpCvdZ { get; set; }
        public double? SpotPerpDiv { get; set; }
        public double? SpotShare { get; set; }
        public double? SpotDeltaShare { get; set; }
        public double? PerpDeltaShare { get; set; }
        public double? SpotVolume { get; set; }
        public double? PerpVolume { get; set; }
        public double? SpotPrice { get; set; }
        public double? PerpPrice { get; set; }

        // Плечо и позиционирование.
        public double? BasisBps { get; set; }
        public double? FundingRate { get; set; }
        public double? FundingRatePct { get; set; }
        public double? FundingPct { get; set; }
        public double? FundingZ { get; set; }
        public double? OiBreadth { get; set; }
        public double? OiBase { get; set; }
        public double? OiUsd { get; set; }
        public double? OiChangePct { get; set; }
        public double? GlobalOiDeltaPct { get; set; }
        public double? MarkPrice { get; set; }
        public double? IndexPrice { get; set; }

        // Принудительные закрытия.
        public double? LiqLongUsd { get; set; }
        public double? LiqShortUsd { get; set; }
        public double? LiqImbalance { get; set; }
        public double? LiqIntensity { get; set; }

        // Микроструктура. Может отсутствовать, если провайдер не включён.
        public double? BookImbalance { get; set; }
        public double? Ofi { get; set; }

        public ContextFeatures Clone()
        {
            return (ContextFeatures)MemberwiseClone();
        }
    }

    public sealed class SlowContext
    {
        // BTC/ETH-режим и макро-контекст. Обновляются существенно реже.
        public string BtcRegime { get; set; }
        public double? OptionsGamma { get; set; }
        public double? OptionsSkew { get; set; }
        public double? EtfFlowUsd { get; set; }
        public double? StablecoinFlowUsd { get; set; }
        public double? CmeBasisBps { get; set; }

        public SlowContext Clone()
        {
            return (SlowContext)MemberwiseClone();
        }
    }

    public sealed class QualityFeatures
    {
        public bool Stale { get; set; }
        public int FreshVenues { get; set; }
        public long? SpotAgeMs { get; set; }
        public long? OiAgeMs { get; set; }
        public long? FundingAgeMs { get; set; }
        public bool PartialLiquidations { get; set; }
        public string Source { get; set; }
        public string[] Warnings { get; set; }

        public QualityFeatures Clone()
        {
            return new QualityFeatures
            {
                Stale = Stale,
                FreshVenues = FreshVenues,
                SpotAgeMs = SpotAgeMs,
                OiAgeMs = OiAgeMs,
                FundingAgeMs = FundingAgeMs,
                PartialLiquidations = PartialLiquidations,
                Source = Source,
                Warnings = Warnings == null ? null : (string[])Warnings.Clone(),
            };
        }
    }

    public sealed class HubStatus
    {
        public string Type { get; set; } = "status";
        public string State { get; set; } = "stopped";
        public int Port { get; set; }
        public string Endpoint { get; set; }
        public string Version { get; set; }
        public long UptimeMs { get; set; }
        public long StartedAtMs { get; set; }
        public int Clients { get; set; }
        public int Subscriptions { get; set; }
        public int FeatureSeries { get; set; }
        public int FeatureBars { get; set; }
        public int StoredOiPoints { get; set; }
        public int StoredFeatureBars { get; set; }
        public int Connectors { get; set; }
        public string LastError { get; set; }
    }

    public sealed class LiquidationMessage
    {
        public string Type { get; set; } = "liquidation";
        public string Instrument { get; set; } = "";
        public long TimeMs { get; set; }
        public bool IsLong { get; set; }
        public double Price { get; set; }
        public double Quantity { get; set; }
        public double Notional { get; set; }
        public string Source { get; set; } = "BINANCE";
    }

    public sealed class HubRequest
    {
        public string Type { get; set; }
        public string Instrument { get; set; }
        public string Timeframe { get; set; }
        public string Feature { get; set; }
        public long? FromMs { get; set; }
        public long? ToMs { get; set; }
        public int? Limit { get; set; }
    }

    public sealed class ConnectorStatus
    {
        public string Name { get; set; } = "";
        public string State { get; set; } = "stopped";
        public long LastMessageMs { get; set; }
        public long Reconnects { get; set; }
        public string LastError { get; set; }
    }
}
