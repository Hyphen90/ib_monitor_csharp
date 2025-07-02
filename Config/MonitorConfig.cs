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

        [JsonProperty("marketoffset")]
        public string? MarketOffset { get; set; } = "2%";

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

        [JsonProperty("bartrailingleokback")]
        public int BarTrailingLookback { get; set; } = 0;

        [JsonProperty("barinterval")]
        public int BarInterval { get; set; } = 10;

        /// <summary>
        /// Parses the MarketOffset string to a double value.
        /// Supports both absolute values (e.g., "0.05") and percentage values (e.g., "2%").
        /// </summary>
        public double GetMarketOffsetValue(double basePrice)
        {
            if (string.IsNullOrEmpty(MarketOffset))
                return basePrice * 0.02; // Default 2%

            var offsetStr = MarketOffset.Trim();
            
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

            // Fallback to 2% if parsing fails
            return basePrice * 0.02;
        }

        public override string ToString()
        {
            return JsonConvert.SerializeObject(this, Formatting.Indented);
        }
    }
}
