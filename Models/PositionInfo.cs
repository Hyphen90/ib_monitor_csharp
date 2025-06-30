using IBApi;

namespace IBMonitor.Models
{
    public class PositionInfo
    {
        public Contract Contract { get; set; } = new Contract();
        public decimal Quantity { get; set; }
        public double AveragePrice { get; set; }
        public double MarketPrice { get; set; }
        public double MarketValue { get; set; }
        public double UnrealizedPnL { get; set; }
        public double RealizedPnL { get; set; }
        public DateTime LastUpdate { get; set; } = DateTime.Now;
        
        // Position timing tracking
        public DateTime? FirstFillTimestamp { get; set; }
        
        // Stop-Loss tracking
        public int? StopLossOrderId { get; set; }
        public double? StopLossPrice { get; set; }
        public double? StopLimitPrice { get; set; }
        
        // Break-Even tracking
        public int? BreakEvenOrderId { get; set; }
        public bool BreakEvenTriggered { get; set; }
        public double? BreakEvenTriggerPrice { get; set; }

        public bool IsLongPosition => Quantity > 0;
        public bool IsFlat => Quantity == 0;

        public override string ToString()
        {
            return $"{Contract.Symbol} - Qty: {Quantity}, AvgPrice: {AveragePrice:F2}, Market: {MarketPrice:F2}, PnL: {UnrealizedPnL:F2}";
        }
    }
} 