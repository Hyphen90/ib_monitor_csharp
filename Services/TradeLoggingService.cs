using IBMonitor.Models;
using Serilog;
using System.Text;

namespace IBMonitor.Services
{
    public class TradeLoggingService
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, TradeInfo> _activeTrades = new(); // Symbol -> TradeInfo
        private readonly object _lockObject = new object();
        private readonly string _logDirectory;

        public TradeLoggingService(ILogger logger)
        {
            _logger = logger;
            // Create base trade logs directory
            _logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Log_Trades");
            Directory.CreateDirectory(_logDirectory);
        }

        /// <summary>
        /// Records when a new position is opened (flat -> long)
        /// </summary>
        public void RecordPositionOpened(string symbol, decimal quantity, double avgPrice, double bidPrice, double askPrice, DateTime? openTime = null)
        {
            lock (_lockObject)
            {
                // Only track long positions for now
                if (quantity <= 0) return;

                // Use current market price (ask for buy orders) as the reference price
                var currentMarketPrice = askPrice > 0 ? askPrice : avgPrice;

                var trade = new TradeInfo
                {
                    Symbol = symbol,
                    Quantity = quantity,
                    OrderType = "Market", // Will be updated if we know the actual order type
                    Price = currentMarketPrice, // Current market price at time of trade creation
                    DateTimeCreated = openTime ?? DateTime.Now,
                    BidTriggered = bidPrice,
                    AskTriggered = askPrice,
                    DateTimeExecuted = null, // Will be set from order execution event
                    PriceExecuted = null, // Will be set from order execution event
                    BuySell = "Buy", // Long position = Buy
                    IsExecuted = false, // Will be set to true when order execution is recorded
                    IsClosed = false
                };

                _activeTrades[symbol] = trade;
                
                _logger.Information("Trade opened for logging: {Symbol} {Quantity} @ market price {Price:F4}", 
                    symbol, quantity, currentMarketPrice);
            }
        }

        /// <summary>
        /// Records when a position is closed (long -> flat)
        /// </summary>
        public void RecordPositionClosed(string symbol, double closePrice, DateTime? closeTime = null)
        {
            lock (_lockObject)
            {
                if (_activeTrades.TryGetValue(symbol, out var trade))
                {
                    trade.DateTimeClosed = closeTime ?? DateTime.Now;
                    
                    // Only set PriceClosed if not already set by SELL execution
                    // RecordSellOrderExecution provides the authoritative sell price from execDetails
                    if (!trade.PriceClosed.HasValue || trade.PriceClosed == 0.0)
                    {
                        trade.PriceClosed = closePrice;
                        _logger.Debug("Setting close price from position update: {Symbol} @ {ClosePrice:F4}", symbol, closePrice);
                    }
                    else
                    {
                        _logger.Debug("Close price already set by SELL execution: {Symbol} @ {ExistingPrice:F4} (ignoring position close price {ClosePrice:F4})", 
                            symbol, trade.PriceClosed.Value, closePrice);
                    }
                    
                    trade.IsClosed = true;
                    
                    // Calculate resulting points
                    trade.CalculateResultingPoints();
                    
                    // DON'T remove from active tracking yet - wait for SELL execDetails
                    // WriteTradeToLog and removal will happen in RecordSellOrderExecution
                    
                    _logger.Information("Trade marked as closed for logging: {Symbol} @ {ClosePrice:F4} = {ResultingPoints:F4} points (waiting for SELL execDetails)", 
                        symbol, trade.PriceClosed ?? closePrice, trade.ResultingPoints ?? 0);
                }
                else
                {
                    _logger.Warning("Attempted to close trade for {Symbol} but no active trade found", symbol);
                }
            }
        }

        /// <summary>
        /// Records BUY order execution details (trade entry)
        /// </summary>
        public void RecordBuyOrderExecution(int orderId, string symbol, double avgFillPrice, DateTime? executionTime = null)
        {
            lock (_lockObject)
            {
                if (_activeTrades.TryGetValue(symbol, out var trade))
                {
                    trade.DateTimeExecuted = executionTime ?? DateTime.Now;
                    trade.PriceExecuted = avgFillPrice;
                    trade.IsExecuted = true;
                    trade.OrderId = orderId;
                    
                    // Calculate time and price differences now that we have execution data
                    trade.CalculateTimeDifference();
                    trade.CalculatePriceDifference();
                    
                    _logger.Information("BUY order execution recorded for {Symbol}: OrderId={OrderId} ExecutedPrice={ExecutedPrice:F4} TimeDiff={TimeDiff:F3}s PriceDiff={PriceDiff:F4}", 
                        symbol, orderId, avgFillPrice, trade.TimeDifference ?? 0, trade.PriceDifference ?? 0);
                }
                else
                {
                    _logger.Warning("BUY order execution received for {Symbol} OrderId={OrderId} but no active trade found", symbol, orderId);
                }
            }
        }

        /// <summary>
        /// Records SELL order execution details (trade exit)
        /// </summary>
        public void RecordSellOrderExecution(int orderId, string symbol, double avgFillPrice, DateTime? executionTime = null)
        {
            lock (_lockObject)
            {
                if (_activeTrades.TryGetValue(symbol, out var trade))
                {
                    // Update the close price with the actual sell execution price
                    trade.PriceClosed = avgFillPrice;
                    trade.DateTimeClosed = executionTime ?? DateTime.Now;
                    
                    // Recalculate resulting points with the actual sell price
                    trade.CalculateResultingPoints();
                    
                    _logger.Information("SELL order execution recorded for {Symbol}: OrderId={OrderId} SellPrice={SellPrice:F4} ResultingPoints={ResultingPoints:F4}", 
                        symbol, orderId, avgFillPrice, trade.ResultingPoints ?? 0);
                    
                    // Now that we have the final SELL execution details, complete the trade
                    WriteTradeToLog(trade);
                    
                    // Remove from active tracking
                    _activeTrades.Remove(symbol);
                    
                    _logger.Information("Trade completed and logged: {Symbol} @ {ClosePrice:F4} = {ResultingPoints:F4} points", 
                        symbol, avgFillPrice, trade.ResultingPoints ?? 0);
                }
                else
                {
                    _logger.Warning("SELL order execution received for {Symbol} OrderId={OrderId} but no active trade found", symbol, orderId);
                }
            }
        }

        /// <summary>
        /// Updates position size during an active trade (for future use if needed)
        /// </summary>
        public void UpdatePositionSize(string symbol, decimal newQuantity, double newAvgPrice)
        {
            lock (_lockObject)
            {
                if (_activeTrades.TryGetValue(symbol, out var trade))
                {
                    // For now, we ignore additional purchases as requested
                    // This method is here for potential future use
                    _logger.Debug("Position size update ignored for {Symbol}: {OldQty} -> {NewQty}", 
                        symbol, trade.Quantity, newQuantity);
                }
            }
        }

        /// <summary>
        /// Writes a completed trade to the daily log file in symbol-specific directory
        /// </summary>
        private void WriteTradeToLog(TradeInfo trade)
        {
            try
            {
                // Create symbol-specific directory: Log_Trades/ACB/
                var symbolDirectory = Path.Combine(_logDirectory, trade.Symbol);
                Directory.CreateDirectory(symbolDirectory);
                
                // New filename format: 20250930.csv (without prefix, with .csv extension)
                var today = DateTime.Now.ToString("yyyyMMdd");
                var logFileName = $"{today}.csv";
                var logFilePath = Path.Combine(symbolDirectory, logFileName);
                
                // Check if file exists, if not create it with header
                bool fileExists = File.Exists(logFilePath);
                
                using (var writer = new StreamWriter(logFilePath, append: true, Encoding.UTF8))
                {
                    // Write header if this is a new file
                    if (!fileExists)
                    {
                        writer.WriteLine(TradeInfo.GetCsvHeader());
                    }
                    
                    // Write trade data
                    writer.WriteLine(trade.ToCsvLine());
                }
                
                _logger.Information("Trade logged to file: {LogFile} - {Symbol} {BuySell} {Quantity} @ {ExecutedPrice:F2} -> {ClosePrice:F2} = {ResultingPoints:F1} points", 
                    Path.Combine(trade.Symbol, logFileName), trade.Symbol, trade.BuySell, trade.Quantity, trade.PriceExecuted ?? 0, trade.PriceClosed ?? 0, trade.ResultingPoints ?? 0);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error writing trade to log file: {Symbol}", trade.Symbol);
            }
        }

        /// <summary>
        /// Gets the count of currently active trades
        /// </summary>
        public int GetActiveTradeCount()
        {
            lock (_lockObject)
            {
                return _activeTrades.Count;
            }
        }

        /// <summary>
        /// Gets information about currently active trades
        /// </summary>
        public string GetActiveTradesInfo()
        {
            lock (_lockObject)
            {
                if (_activeTrades.Count == 0)
                {
                    return "No active trades being tracked.";
                }

                var info = new StringBuilder();
                info.AppendLine($"Active trades being tracked: {_activeTrades.Count}");
                
                foreach (var trade in _activeTrades.Values)
                {
                    var duration = DateTime.Now - trade.DateTimeCreated;
                    info.AppendLine($"  {trade.Symbol}: {trade.BuySell} {trade.Quantity} @ {trade.PriceExecuted:F2} (Duration: {duration:hh\\:mm\\:ss})");
                }
                
                return info.ToString().TrimEnd();
            }
        }

        /// <summary>
        /// Checks if there's an active trade for the given symbol
        /// </summary>
        public bool HasActiveTrade(string symbol)
        {
            lock (_lockObject)
            {
                return _activeTrades.ContainsKey(symbol);
            }
        }

        /// <summary>
        /// Gets the active trade for a symbol (if any)
        /// </summary>
        public TradeInfo? GetActiveTrade(string symbol)
        {
            lock (_lockObject)
            {
                return _activeTrades.TryGetValue(symbol, out var trade) ? trade : null;
            }
        }
    }
}
