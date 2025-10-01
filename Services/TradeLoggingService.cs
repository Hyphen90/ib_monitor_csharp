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
            RecordBuyOrderExecution(orderId, symbol, avgFillPrice, 0m, executionTime); // Use 0 quantity as fallback
        }

        /// <summary>
        /// Records BUY order execution details with quantity (trade entry)
        /// </summary>
        public void RecordBuyOrderExecution(int orderId, string symbol, double avgFillPrice, decimal quantity, DateTime? executionTime = null)
        {
            lock (_lockObject)
            {
                if (_activeTrades.TryGetValue(symbol, out var trade))
                {
                    var execTime = executionTime ?? DateTime.Now;
                    
                    // Add this execution to the buy executions list
                    trade.BuyExecutions.Add(new ExecutionDetails
                    {
                        OrderId = orderId,
                        Quantity = quantity,
                        Price = avgFillPrice,
                        ExecutionTime = execTime
                    });
                    
                    // Update average buy price with all executions
                    trade.UpdateAverageBuyPrice();
                    
                    // Set execution timing for the first buy execution
                    if (!trade.DateTimeExecuted.HasValue)
                    {
                        trade.DateTimeExecuted = execTime;
                        trade.OrderId = orderId;
                        
                        // Calculate time and price differences based on average price
                        trade.CalculateTimeDifference();
                        trade.CalculatePriceDifference();
                    }
                    
                    trade.IsExecuted = true;
                    
                    _logger.Information("BUY order execution recorded for {Symbol}: OrderId={OrderId} ExecutedPrice={ExecutedPrice:F4} Qty={Quantity} AvgBuyPrice={AvgPrice:F4} TotalBuyExecs={TotalExecs}", 
                        symbol, orderId, avgFillPrice, quantity, trade.PriceExecuted ?? 0, trade.BuyExecutions.Count);
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
            RecordSellOrderExecution(orderId, symbol, avgFillPrice, 0m, executionTime); // Use 0 quantity as fallback
        }

        /// <summary>
        /// Records SELL order execution details with quantity (trade exit)
        /// </summary>
        public void RecordSellOrderExecution(int orderId, string symbol, double avgFillPrice, decimal quantity, DateTime? executionTime = null)
        {
            lock (_lockObject)
            {
                if (_activeTrades.TryGetValue(symbol, out var trade))
                {
                    var execTime = executionTime ?? DateTime.Now;
                    
                    // Add this execution to the sell executions list
                    trade.SellExecutions.Add(new ExecutionDetails
                    {
                        OrderId = orderId,
                        Quantity = quantity,
                        Price = avgFillPrice,
                        ExecutionTime = execTime
                    });
                    
                    // Update average sell price with all executions
                    trade.UpdateAverageSellPrice();
                    
                    // Set close timing for the first sell execution
                    if (!trade.DateTimeClosed.HasValue)
                    {
                        trade.DateTimeClosed = execTime;
                    }
                    
                    // Calculate total sold quantity to check if position is fully closed
                    var totalSoldQuantity = trade.SellExecutions.Sum(e => e.Quantity);
                    var totalBoughtQuantity = trade.BuyExecutions.Sum(e => e.Quantity);
                    
                    _logger.Information("SELL order execution recorded for {Symbol}: OrderId={OrderId} SellPrice={SellPrice:F4} Qty={Quantity} AvgSellPrice={AvgPrice:F4} TotalSellExecs={TotalExecs} SoldQty={SoldQty}/{BoughtQty}", 
                        symbol, orderId, avgFillPrice, quantity, trade.PriceClosed ?? 0, trade.SellExecutions.Count, totalSoldQuantity, totalBoughtQuantity);
                    
                    // Check if position is fully closed (all bought shares have been sold)
                    if (totalSoldQuantity >= totalBoughtQuantity && totalBoughtQuantity > 0)
                    {
                        // Position is fully closed - complete the trade
                        trade.CalculateResultingPoints();
                        
                        _logger.Information("Position fully closed for {Symbol}: AvgBuyPrice={AvgBuyPrice:F4} AvgSellPrice={AvgSellPrice:F4} ResultingPoints={ResultingPoints:F4}", 
                            symbol, trade.PriceExecuted ?? 0, trade.PriceClosed ?? 0, trade.ResultingPoints ?? 0);
                        
                        // Complete the trade
                        WriteTradeToLog(trade);
                        
                        // Remove from active tracking
                        _activeTrades.Remove(symbol);
                        
                        _logger.Information("Trade completed and logged: {Symbol} @ AvgSellPrice {ClosePrice:F4} = {ResultingPoints:F4} points", 
                            symbol, trade.PriceClosed ?? 0, trade.ResultingPoints ?? 0);
                    }
                    else
                    {
                        _logger.Debug("Position partially closed for {Symbol}: {SoldQty}/{BoughtQty} shares sold", 
                            symbol, totalSoldQuantity, totalBoughtQuantity);
                    }
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
