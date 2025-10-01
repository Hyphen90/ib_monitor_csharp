using IBApi;

namespace IBMonitor.Models
{
    public class TradeInfo
    {
        public string Symbol { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public string OrderType { get; set; } = string.Empty;
        public double Price { get; set; }
        public DateTime DateTimeCreated { get; set; }
        public double BidTriggered { get; set; }
        public double AskTriggered { get; set; }
        public DateTime? DateTimeExecuted { get; set; }
        public double? PriceExecuted { get; set; }
        public string BuySell { get; set; } = string.Empty;
        public double? TimeDifference { get; set; } // in seconds
        public double? PriceDifference { get; set; }
        public DateTime? DateTimeClosed { get; set; }
        public double? PriceClosed { get; set; }
        public double? ResultingPoints { get; set; }
        
        // Additional tracking fields
        public int OrderId { get; set; }
        public bool IsExecuted { get; set; }
        public bool IsClosed { get; set; }
        
        // Multiple executions tracking for average price calculation
        public List<ExecutionDetails> BuyExecutions { get; set; } = new();
        public List<ExecutionDetails> SellExecutions { get; set; } = new();
        
        /// <summary>
        /// Calculates the time difference in seconds between order creation and execution
        /// </summary>
        public void CalculateTimeDifference()
        {
            if (DateTimeExecuted.HasValue)
            {
                TimeDifference = (DateTimeExecuted.Value - DateTimeCreated).TotalSeconds;
            }
        }
        
        /// <summary>
        /// Calculates the price difference between order price and executed price
        /// </summary>
        public void CalculatePriceDifference()
        {
            if (PriceExecuted.HasValue)
            {
                PriceDifference = PriceExecuted.Value - Price;
            }
        }
        
        /// <summary>
        /// Calculates the resulting points (profit/loss) when trade is closed
        /// </summary>
        public void CalculateResultingPoints()
        {
            if (PriceExecuted.HasValue && PriceClosed.HasValue)
            {
                if (BuySell == "Buy")
                {
                    // For buy orders: profit = close price - executed price
                    ResultingPoints = PriceClosed.Value - PriceExecuted.Value;
                }
                else if (BuySell == "SellShort")
                {
                    // For sell short orders: profit = executed price - close price
                    ResultingPoints = PriceExecuted.Value - PriceClosed.Value;
                }
            }
        }
        
        /// <summary>
        /// Calculates the weighted average price from a list of executions
        /// </summary>
        private double CalculateWeightedAveragePrice(List<ExecutionDetails> executions)
        {
            if (executions == null || executions.Count == 0)
                return 0.0;
            
            double totalValue = 0.0;
            decimal totalQuantity = 0m;
            
            foreach (var execution in executions)
            {
                totalValue += execution.Price * (double)execution.Quantity;
                totalQuantity += execution.Quantity;
            }
            
            return totalQuantity > 0 ? totalValue / (double)totalQuantity : 0.0;
        }
        
        /// <summary>
        /// Updates PriceExecuted with the weighted average of all buy executions
        /// </summary>
        public void UpdateAverageBuyPrice()
        {
            if (BuyExecutions.Count > 0)
            {
                PriceExecuted = CalculateWeightedAveragePrice(BuyExecutions);
            }
        }
        
        /// <summary>
        /// Updates PriceClosed with the weighted average of all sell executions
        /// </summary>
        public void UpdateAverageSellPrice()
        {
            if (SellExecutions.Count > 0)
            {
                PriceClosed = CalculateWeightedAveragePrice(SellExecutions);
            }
        }
        
        /// <summary>
        /// Gets the total quantity from all buy executions
        /// </summary>
        public decimal GetTotalBuyQuantity()
        {
            return BuyExecutions.Sum(e => e.Quantity);
        }
        
        /// <summary>
        /// Gets the total quantity from all sell executions
        /// </summary>
        public decimal GetTotalSellQuantity()
        {
            return SellExecutions.Sum(e => e.Quantity);
        }
        
        /// <summary>
        /// Formats a price with appropriate decimal places (4 for under $1, minimum 2 otherwise)
        /// </summary>
        private string FormatPrice(double price)
        {
            if (price < 1.0)
            {
                return price.ToString("0.0000");
            }
            else
            {
                return price.ToString("0.##");
            }
        }
        
        /// <summary>
        /// Formats a nullable price with appropriate decimal places
        /// </summary>
        private string FormatPrice(double? price)
        {
            if (!price.HasValue) return "";
            return FormatPrice(price.Value);
        }
        
        /// <summary>
        /// Formats price differences and resulting points based on the stock price level
        /// Uses the higher of executed price or closed price to determine formatting
        /// </summary>
        private string FormatPriceDifference(double? value)
        {
            if (!value.HasValue) return "";
            
            // Determine formatting based on the stock price level
            var referencePrice = Math.Max(PriceExecuted ?? 0, PriceClosed ?? 0);
            if (referencePrice == 0) referencePrice = Price; // Fallback to order price
            
            if (referencePrice < 1.0)
            {
                return value.Value.ToString("0.0000");
            }
            else
            {
                return value.Value.ToString("0.##");
            }
        }
        
        /// <summary>
        /// Formats the trade info as CSV line with semicolon separator
        /// </summary>
        public string ToCsvLine()
        {
            var dateCreated = DateTimeCreated.ToString("yyyy.MM.dd HH:mm:ss.fff");
            var dateExecuted = DateTimeExecuted?.ToString("yyyy.MM.dd HH:mm:ss.fff") ?? "";
            var dateClosed = DateTimeClosed?.ToString("yyyy.MM.dd HH:mm:ss.fff") ?? "";
            
            var timeDiff = TimeDifference?.ToString("F3") ?? "";
            var priceDiff = FormatPriceDifference(PriceDifference);
            var resultPoints = FormatPriceDifference(ResultingPoints);
            var priceExec = FormatPrice(PriceExecuted);
            var priceClose = FormatPrice(PriceClosed);
            
            // Use total buy quantity from all executions instead of original Quantity
            var totalQuantity = GetTotalBuyQuantity();
            
            return $"{Symbol};{totalQuantity};{OrderType};{FormatPrice(Price)};{dateCreated};{FormatPrice(BidTriggered)};{FormatPrice(AskTriggered)};{dateExecuted};{priceExec};{BuySell};{timeDiff};{priceDiff};{dateClosed};{priceClose};{resultPoints}";
        }
        
        /// <summary>
        /// Gets the CSV header line
        /// </summary>
        public static string GetCsvHeader()
        {
            return "Symbol;Quantity;Order Type;Price;DateTime Created;Bid Triggerd;Ask Triggered;DateTime Executed;Price Executed;Buy/Sell;Time Difference;Price Difference;DateTime Closed;Price Closed;Resulting Points";
        }
    }
}
