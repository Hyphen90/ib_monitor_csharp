using Newtonsoft.Json;

namespace IBMonitor.Config
{
    public class MonitorConfig
    {
        [JsonProperty("port")]
        public int Port { get; set; } = 7497;

        [JsonProperty("clientid")]
        public int ClientId { get; set; } = 1;

        [JsonProperty("stoploss")]
        public double StopLoss { get; set; } = 0.20;

        [JsonProperty("buyoffset")]
        public string? BuyOffset { get; set; } = "0.10";

        [JsonProperty("selloffset")]
        public string? SellOffset { get; set; } = "0.10";

        [JsonProperty("breakeven")]
        public double? BreakEven { get; set; }

        [JsonProperty("breakevenoffset")]
        public double BreakEvenOffset { get; set; } = 0.01;

        [JsonProperty("symbol")]
        public string? Symbol { get; set; }

        [JsonProperty("positionopenscript")]
        public string? PositionOpenScript { get; set; }

        [JsonProperty("loglevel")]
        public string LogLevel { get; set; } = "INFO";

        [JsonProperty("logfile")]
        public bool LogFile { get; set; } = false;

        [JsonProperty("maxshares")]
        public int? MaxShares { get; set; } = null; // null = unlimited

        [JsonProperty("usebarbasedtrailing")]
        public bool UseBarBasedTrailing { get; set; } = false;

        [JsonProperty("bartrailingoffset")]
        public double BarTrailingOffset { get; set; } = 0.05;

        [JsonProperty("bartrailinglookback")]
        public int BarTrailingLookback { get; set; } = 0;

        [JsonProperty("barinterval")]
        public int BarInterval { get; set; } = 10;

        [JsonProperty("bardebug")]
        public bool BarDebug { get; set; } = false;

        /// <summary>
        /// Parses the BuyOffset string to a double value.
        /// Supports both absolute values (e.g., "0.05") and percentage values (e.g., "2%").
        /// </summary>
        public double GetBuyOffsetValue(double basePrice)
        {
            if (string.IsNullOrEmpty(BuyOffset))
                return 0.10; // Default 0.10

            var offsetStr = BuyOffset.Trim();
            
            if (offsetStr.EndsWith("%"))
            {
                var percentStr = offsetStr.Substring(0, offsetStr.Length - 1);
                if (double.TryParse(percentStr, out var percent))
                {
                    return basePrice * (percent / 100.0);
                }
            }
            else if (double.TryParse(offsetStr, out var absoluteValue))
            {
                return absoluteValue;
            }

            // Fallback to 0.10 if parsing fails
            return 0.10;
        }

        /// <summary>
        /// Parses the SellOffset string to a double value.
        /// Supports both absolute values (e.g., "0.05") and percentage values (e.g., "2%").
        /// </summary>
        public double GetSellOffsetValue(double basePrice)
        {
            if (string.IsNullOrEmpty(SellOffset))
                return 0.10; // Default 0.10

            var offsetStr = SellOffset.Trim();
            
            if (offsetStr.EndsWith("%"))
            {
                var percentStr = offsetStr.Substring(0, offsetStr.Length - 1);
                if (double.TryParse(percentStr, out var percent))
                {
                    return basePrice * (percent / 100.0);
                }
            }
            else if (double.TryParse(offsetStr, out var absoluteValue))
            {
                return absoluteValue;
            }

            // Fallback to 0.10 if parsing fails
            return 0.10;
        }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }
    }
}
