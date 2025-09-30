using IBApi;
using Serilog;
using IBMonitor.Config;
using IBMonitor.Models;
using System.Diagnostics;

namespace IBMonitor.Services
{
    public class PositionMonitorService
    {
        private readonly ILogger _logger;
        private readonly MonitorConfig _config;
        private readonly IBConnectionService _ibService;
        private readonly RealTimeBarService _realTimeBarService;
        private readonly BarTrailingStopManager _barTrailingStopManager;
        private readonly TradeLoggingService _tradeLoggingService;
        private readonly Dictionary<string, PositionInfo> _positions = new();
        private readonly Dictionary<int, int> _orderToPositionMap = new(); // OrderId -> Position tracking
        private readonly HashSet<int> _activeSellOrderIds = new(); // Track active sell orders to prevent stop-loss interference
        private readonly object _lockObject = new object();
        private int _marketDataTickerId = -1;
        private const int MARKET_DATA_TICKER_ID = 1000; // Fixed ticker ID for symbol market data
        private double _currentAskPrice = 0.0;
        private bool _isClosing = false;
        
        // Take-Profit for flat state (target can be set before position is active)
        private double? _flatStateTakeProfitPrice = null;
        private bool _flatStateTakeProfitActive = false;

        public event Action<PositionInfo>? PositionOpened;
        public event Action<PositionInfo>? PositionClosed;
        public event Action<PositionInfo>? PositionChanged;

        public PositionMonitorService(ILogger logger, MonitorConfig config, IBConnectionService ibService)
        {
            _logger = logger;
            _config = config;
            _ibService = ibService;

            // Initialize services
            _realTimeBarService = new RealTimeBarService(_logger, _config, _ibService);
            _barTrailingStopManager = new BarTrailingStopManager(_logger, _config);
            _tradeLoggingService = new TradeLoggingService(_logger);

            // Subscribe to IB events
            _ibService.PositionUpdate += OnPositionUpdate;
            _ibService.OrderStatusUpdate += OnOrderStatusUpdate;
            _ibService.OpenOrderReceived += OnOpenOrderReceived;
            _ibService.ExecutionReceived += OnExecutionReceived;
            _ibService.TickPriceReceived += OnTickPriceReceived;
            _ibService.Connected += OnIBConnected;
            _ibService.RealTimeBarReceived += OnRealTimeBarReceived;

            // Subscribe to real-time bar events
            _realTimeBarService.RealTimeBarReceived += OnRealTimeBarFromService;
        }

        private void OnPositionUpdate(string account, Contract contract, decimal position, double avgCost)
        {
            // Only monitor the configured symbol
            if (string.IsNullOrEmpty(_config.Symbol) || 
                !contract.Symbol.Equals(_config.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            lock (_lockObject)
            {
                var key = GetPositionKey(contract);
                var isNewPosition = !_positions.ContainsKey(key);
                var wasFlat = isNewPosition || _positions[key].IsFlat;
                var isNowFlat = position == 0;

                if (isNewPosition && position != 0)
                {
                    // New position detected
                    var positionInfo = new PositionInfo
                    {
                        Contract = contract,
                        Quantity = position,
                        AveragePrice = avgCost,
                        LastUpdate = DateTime.Now,
                        FirstFillTimestamp = DateTime.Now  // Set first fill timestamp when position is first detected
                    };

                    _positions[key] = positionInfo;
                    _logger.Information("New position detected: {Symbol} - Qty: {Quantity}, AvgPrice: {AvgPrice:F2}", 
                        contract.Symbol, position, avgCost);
                    
                    // Record trade opening for logging (flat -> long position)
                    _tradeLoggingService.RecordPositionOpened(contract.Symbol, position, avgCost, 
                        _currentBidPrice, _currentAskPrice, DateTime.Now);
                    
                    // Check if there's a flat state take-profit target to activate
                    if (_flatStateTakeProfitActive && _flatStateTakeProfitPrice.HasValue)
                    {
                        // Safety check: Only activate target if entry price allows profitable exit
                        // For long positions: entry price must be below target
                        // For short positions: entry price must be above target
                        bool unsafeTarget = (position > 0 && avgCost >= _flatStateTakeProfitPrice.Value) ||
                                           (position < 0 && avgCost <= _flatStateTakeProfitPrice.Value);
                        
                        if (unsafeTarget)
                        {
                            // Entry price is at or above target - delete target for safety
                            _logger.Warning("Flat state take-profit target deleted for safety: Entry price {EntryPrice:F2} makes target {TargetPrice:F2} unprofitable for {Symbol} ({PositionType})", 
                                avgCost, _flatStateTakeProfitPrice.Value, contract.Symbol, position > 0 ? "LONG" : "SHORT");
                            
                            // Clear flat state target without activation
                            _flatStateTakeProfitPrice = null;
                            _flatStateTakeProfitActive = false;
                        }
                        else
                        {
                            // Safe to activate target
                            positionInfo.TakeProfitPrice = _flatStateTakeProfitPrice.Value;
                            positionInfo.TakeProfitActive = true;
                            _logger.Information("Flat state take-profit target activated for new position {Symbol}: Target price {TargetPrice:F2} (Entry: {EntryPrice:F2})", 
                                contract.Symbol, _flatStateTakeProfitPrice.Value, avgCost);
                            
                            // Clear flat state target after activation
                            _flatStateTakeProfitPrice = null;
                            _flatStateTakeProfitActive = false;
                        }
                    }
                    
                    // Execute position open script if configured
                    ExecutePositionOpenScript(positionInfo);
                    
                    // Create stop-loss order for long positions only (but not during close mode)
                    if (position > 0 && !_isClosing)
                    {
                        CreateStopLossOrder(positionInfo);
                    }
                    else if (position > 0 && _isClosing)
                    {
                        _logger.Debug("Skipping stop-loss creation for {Symbol} - close mode is active", contract.Symbol);
                    }

                    PositionOpened?.Invoke(positionInfo);
                }
                else if (!isNewPosition)
                {
                    var existingPosition = _positions[key];
                    var positionSizeChanged = existingPosition.Quantity != position;
                    var avgPriceChanged = Math.Abs(existingPosition.AveragePrice - avgCost) > 0.001;

                    // Update position info
                    existingPosition.Quantity = position;
                    existingPosition.LastUpdate = DateTime.Now;

                    if (avgPriceChanged)
                    {
                        var priceChangePerShare = Math.Abs(existingPosition.AveragePrice - avgCost);
                        _logger.Information("Average price changed from {OldPrice:F2} to {NewPrice:F2} for {Symbol}",
                            existingPosition.AveragePrice, avgCost, contract.Symbol);
                        existingPosition.AveragePrice = avgCost;
                        
                        // Only update stop-loss if price change is more than 1 cent per share (to avoid commission-only adjustments)
                        if (position > 0 && priceChangePerShare >= 0.02)
                        {
                            UpdateStopLossOrder(existingPosition);
                        }
                    }

                    if (isNowFlat && !wasFlat)
                    {
                        // Position closed - record trade closure for logging (long -> flat position)
                        _tradeLoggingService.RecordPositionClosed(contract.Symbol, avgCost, DateTime.Now);
                        
                        // Reset break-even trigger for future positions
                        existingPosition.BreakEvenTriggered = false;
                        CancelExistingOrders(existingPosition);
                        
                        // Reset take-profit trigger when position goes flat
                        existingPosition.TakeProfitPrice = null;
                        existingPosition.TakeProfitActive = false;
                        _logger.Information("Take-profit trigger reset for {Symbol} - position went flat", contract.Symbol);
                        
                        PositionClosed?.Invoke(existingPosition);
                        
                        // Remove position from dictionary when flat
                        _positions.Remove(key);
                        _logger.Debug("Position {Symbol} removed from tracking - flat position", contract.Symbol);
                    }
                    else if (positionSizeChanged && !isNowFlat)
                    {
                        // Position size changed but still open
                        _logger.Information("Position size changed: {Symbol} {OldQty} -> {NewQty}", 
                            contract.Symbol, existingPosition.Quantity, position);
                        
                        if (position > 0) // Only for long positions
                        {
                            // Check if there are active sell orders that could interfere with stop-loss adjustments
                            if (_activeSellOrderIds.Any())
                            {
                                _logger.Debug("Skipping stop-loss adjustment for {Symbol} - active sell orders detected: {ActiveOrders}", 
                                    contract.Symbol, string.Join(", ", _activeSellOrderIds));
                            }
                            else
                            {
                                // Only modify quantity if we have an existing stop-loss order
                                if (existingPosition.StopLossOrderId.HasValue)
                                {
                                    ModifyStopLossOrderQuantity(existingPosition);
                                }
                                else if (!_isClosing)
                                {
                                    // No existing stop-loss, create a new one (but not during close mode)
                                    CreateStopLossOrder(existingPosition);
                                }
                                else
                                {
                                    _logger.Debug("Skipping stop-loss creation for {Symbol} - close mode is active", contract.Symbol);
                                }
                            }
                            CheckBreakEvenTrigger(existingPosition);
                        }
                        
                        PositionChanged?.Invoke(existingPosition);
                    }
                }

                // Reset closing flag when all positions are flat
                if (_isClosing && _positions.Values.All(p => p.IsFlat))
                {
                    _isClosing = false;
                    _logger.Information("Close mode deactivated - all positions are flat");
                }
            }
        }

        private void CreateStopLossOrder(PositionInfo position)
        {
            if (position.Quantity <= 0) return; // Only for long positions

            var stopPrice = RoundPriceForIB(position.AveragePrice - _config.StopLoss);
            var limitOffset = _config.GetSellOffsetValue(stopPrice);
            var limitPrice = RoundPriceForIB(stopPrice - limitOffset);

            var stopOrder = CreateStopLimitOrder(position.Contract, position.Quantity, stopPrice, limitPrice);
            var orderId = _ibService.GetNextOrderId();

            _ibService.PlaceOrder(orderId, position.Contract, stopOrder);

            position.StopLossOrderId = orderId;
            position.StopLossPrice = stopPrice;
            position.StopLimitPrice = limitPrice;

            _orderToPositionMap[orderId] = GetPositionKey(position.Contract).GetHashCode();

            _logger.Information("Stop-Loss order created: {Symbol} OrderId:{OrderId} Stop:{StopPrice} Limit:{LimitPrice}", 
                position.Contract.Symbol, orderId, FormatPrice(stopPrice), FormatPrice(limitPrice));
        }

        private void UpdateStopLossOrder(PositionInfo position)
        {
            if (position.Quantity <= 0) return; // Only for long positions

            // Calculate new stop prices based on updated average price
            var stopPrice = RoundPriceForIB(position.AveragePrice - _config.StopLoss);
            var limitOffset = _config.GetSellOffsetValue(stopPrice);
            var limitPrice = RoundPriceForIB(stopPrice - limitOffset);

            // If we have an existing order, modify it instead of canceling and recreating
            if (position.StopLossOrderId.HasValue)
            {
                ModifyStopLossOrder(position, stopPrice, limitPrice);
            }
            else
            {
                // No existing order, create a new one
                CreateStopLossOrder(position);
            }
        }

        private void ModifyStopLossOrderQuantity(PositionInfo position)
        {
            if (position.Quantity <= 0) return; // Only for long positions
            
            // If no existing stop-loss order, create a new one
            if (!position.StopLossOrderId.HasValue)
            {
                CreateStopLossOrder(position);
                return;
            }

            // Modify existing order quantity (keep same prices)
            ModifyStopLossOrder(position, position.StopLossPrice ?? 0.0, position.StopLimitPrice ?? 0.0);
        }

        private void ModifyStopLossOrder(PositionInfo position, double stopPrice, double limitPrice)
        {
            if (position.Quantity <= 0 || !position.StopLossOrderId.HasValue) return;

            // Update position info with new prices
            position.StopLossPrice = stopPrice;
            position.StopLimitPrice = limitPrice;

            // Create modified order with same OrderId
            var modifiedOrder = CreateStopLimitOrder(position.Contract, position.Quantity, stopPrice, limitPrice);
            
            // Modify the existing order (same OrderId triggers modification)
            _ibService.PlaceOrder(position.StopLossOrderId.Value, position.Contract, modifiedOrder);

            _logger.Information("Stop-Loss order modified: {Symbol} OrderId:{OrderId} Qty:{Quantity} Stop:{StopPrice} Limit:{LimitPrice}", 
                position.Contract.Symbol, position.StopLossOrderId.Value, position.Quantity, FormatPrice(stopPrice), FormatPrice(limitPrice));
        }

        private void CheckBreakEvenTrigger(PositionInfo position)
        {
            if (!_config.UseBreakEven || position.Quantity <= 0 || !_config.BreakEven.HasValue || position.BreakEvenTriggered) 
                return;

            // Break-even should trigger when market price reaches average price + break-even threshold
            var triggerPrice = position.AveragePrice + _config.BreakEven.Value;
            
            if (position.MarketPrice >= triggerPrice)
            {
                var stopPrice = position.AveragePrice + _config.BreakEvenOffset;
                _logger.Information("Break-Even threshold reached for {Symbol}. Market: {MarketPrice:F2}, Trigger: {TriggerPrice:F2} (AvgPrice + {BreakEven:F2}), Stop: {StopPrice:F2} (AvgPrice + {BreakEvenOffset:F2})", 
                    position.Contract.Symbol, position.MarketPrice, triggerPrice, _config.BreakEven.Value, stopPrice, _config.BreakEvenOffset);
                
                CreateBreakEvenOrder(position);
                position.BreakEvenTriggered = true;
            }
        }

        private void CreateBreakEvenOrder(PositionInfo position)
        {
            if (position.Quantity <= 0) return; // Only for long positions

            var breakEvenPrice = RoundPriceForIB(position.AveragePrice + _config.BreakEvenOffset);
            var limitOffset = _config.GetSellOffsetValue(breakEvenPrice);
            var limitPrice = RoundPriceForIB(breakEvenPrice - limitOffset);

            // If we have an existing stop-loss order, modify it to break-even instead of canceling
            if (position.StopLossOrderId.HasValue)
            {
                // Modify existing order to break-even prices
                ModifyStopLossOrder(position, breakEvenPrice, limitPrice);
                
                // Update break-even tracking
                position.BreakEvenOrderId = position.StopLossOrderId.Value;
                position.BreakEvenTriggerPrice = breakEvenPrice;
            }
            else
            {
                // No existing order, create new break-even order
                var breakEvenOrder = CreateStopLimitOrder(position.Contract, position.Quantity, breakEvenPrice, limitPrice);
                var orderId = _ibService.GetNextOrderId();

                _ibService.PlaceOrder(orderId, position.Contract, breakEvenOrder);

                position.BreakEvenOrderId = orderId;
                position.BreakEvenTriggerPrice = breakEvenPrice;
                position.StopLossOrderId = orderId; // Update to track this as the current stop
                position.StopLossPrice = breakEvenPrice;
                position.StopLimitPrice = limitPrice;

                _orderToPositionMap[orderId] = GetPositionKey(position.Contract).GetHashCode();
            }

            _logger.Information("Break-Even order activated: {Symbol} OrderId:{OrderId} Stop:{StopPrice} Limit:{LimitPrice}", 
                position.Contract.Symbol, position.StopLossOrderId.Value, FormatPrice(breakEvenPrice), FormatPrice(limitPrice));
        }

        private Order CreateStopLimitOrder(Contract contract, decimal quantity, double stopPrice, double limitPrice)
        {
            // Ensure the contract uses SMART routing for stop-loss orders
            contract.Exchange = "SMART";
            contract.PrimaryExch = "";
            
            return new Order
            {
                Action = "SELL", // Only for long positions
                OrderType = "STP LMT",
                TotalQuantity = quantity,
                AuxPrice = stopPrice,
                LmtPrice = limitPrice,
                Tif = "GTC",
                Transmit = true,
                OutsideRth = true,  // Allow execution outside regular trading hours
                UsePriceMgmtAlgo = true  // Enable IB's price management algorithm
            };
        }

        private void CancelExistingOrders(PositionInfo position)
        {
            // Only cancel orders that are still being tracked (i.e., still active)
            if (position.StopLossOrderId.HasValue && _orderToPositionMap.ContainsKey(position.StopLossOrderId.Value))
            {
                _ibService.CancelOrder(position.StopLossOrderId.Value);
                _orderToPositionMap.Remove(position.StopLossOrderId.Value);
                position.StopLossOrderId = null;
                position.StopLossPrice = null;
                position.StopLimitPrice = null;
            }

            if (position.BreakEvenOrderId.HasValue && _orderToPositionMap.ContainsKey(position.BreakEvenOrderId.Value))
            {
                _ibService.CancelOrder(position.BreakEvenOrderId.Value);
                _orderToPositionMap.Remove(position.BreakEvenOrderId.Value);
                position.BreakEvenOrderId = null;
                position.BreakEvenTriggerPrice = null;
            }
        }

        private void OnOrderStatusUpdate(int orderId, string status, decimal filled, decimal remaining, 
            double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice)
        {
            lock (_lockObject)
            {
                // Handle active sell order tracking cleanup
                if (_activeSellOrderIds.Contains(orderId))
                {
                    _logger.Debug("Sell order update: {OrderId} Status:{Status} Filled:{Filled} Remaining:{Remaining}", 
                        orderId, status, filled, remaining);

                    if (status == "Filled" || status == "Cancelled")
                    {
                        _activeSellOrderIds.Remove(orderId);
                        _logger.Debug("Removed sell order {OrderId} from active tracking (Status: {Status}) - stop-loss adjustments now allowed", 
                            orderId, status);
                    }
                }

                // Handle stop-loss order tracking - execDetails handles trade logging
                if (_orderToPositionMap.ContainsKey(orderId))
                {
                    _logger.Debug("Stop-Loss order update: {OrderId} Status:{Status} Filled:{Filled}", 
                        orderId, status, filled);

                    if (status == "Filled" || status == "Cancelled")
                    {
                        _orderToPositionMap.Remove(orderId);
                        
                        // Clear order references from positions when order is filled/cancelled
                        foreach (var position in _positions.Values)
                        {
                            if (position.StopLossOrderId == orderId)
                            {
                                _logger.Debug("Clearing Stop-Loss order reference {OrderId} from position {Symbol} (Status: {Status})", 
                                    orderId, position.Contract.Symbol, status);
                                position.StopLossOrderId = null;
                                position.StopLossPrice = null;
                                position.StopLimitPrice = null;
                            }
                            if (position.BreakEvenOrderId == orderId)
                            {
                                _logger.Debug("Clearing Break-Even order reference {OrderId} from position {Symbol} (Status: {Status})", 
                                    orderId, position.Contract.Symbol, status);
                                position.BreakEvenOrderId = null;
                                position.BreakEvenTriggerPrice = null;
                            }
                        }
                    }
                }
            }
        }

        private void OnExecutionReceived(int reqId, Contract contract, Execution execution)
        {
            // Only process executions for our monitored symbol
            if (string.IsNullOrEmpty(_config.Symbol) || 
                !contract.Symbol.Equals(_config.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _logger.Information("Processing execution for trade logging: {Symbol} OrderId:{OrderId} Side:{Side} Shares:{Shares} Price:{Price:F4} Time:{Time}", 
                contract.Symbol, execution.OrderId, execution.Side, execution.Shares, execution.Price, execution.Time);

            // Parse execution time from IB format (YYYYMMDD HH:MM:SS)
            DateTime executionTime = DateTime.Now;
            if (!string.IsNullOrEmpty(execution.Time))
            {
                if (DateTime.TryParseExact(execution.Time, "yyyyMMdd  HH:mm:ss", null, 
                    System.Globalization.DateTimeStyles.None, out var parsedTime))
                {
                    executionTime = parsedTime;
                }
                else if (DateTime.TryParseExact(execution.Time, "yyyyMMdd HH:mm:ss", null, 
                    System.Globalization.DateTimeStyles.None, out var parsedTime2))
                {
                    executionTime = parsedTime2;
                }
            }

            // Determine if this is a BUY or SELL execution
            if (execution.Side.Equals("BOT", StringComparison.OrdinalIgnoreCase))
            {
                // BUY execution (BOT = Bought)
                _tradeLoggingService.RecordBuyOrderExecution(execution.OrderId, contract.Symbol, execution.Price, executionTime);
            }
            else if (execution.Side.Equals("SLD", StringComparison.OrdinalIgnoreCase))
            {
                // SELL execution (SLD = Sold)
                _tradeLoggingService.RecordSellOrderExecution(execution.OrderId, contract.Symbol, execution.Price, executionTime);
            }
            else
            {
                _logger.Warning("Unknown execution side: {Side} for OrderId:{OrderId} {Symbol}", 
                    execution.Side, execution.OrderId, contract.Symbol);
            }
        }

        private void OnOpenOrderReceived(int orderId, Contract contract, Order order, OrderState orderState)
        {
            // Track existing stop-loss orders on startup
            if (order.OrderType == "STP LMT" && order.Action == "SELL" && 
                contract.Symbol.Equals(_config.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                var key = GetPositionKey(contract);
                if (_positions.ContainsKey(key))
                {
                    var position = _positions[key];
                    if (!position.StopLossOrderId.HasValue)
                    {
                        position.StopLossOrderId = orderId;
                        position.StopLossPrice = order.AuxPrice;
                        position.StopLimitPrice = order.LmtPrice;
                        _orderToPositionMap[orderId] = key.GetHashCode();
                        
                        _logger.Information("Existing Stop-Loss order detected: {Symbol} OrderId:{OrderId}", 
                            contract.Symbol, orderId);
                    }
                }
            }
        }

        private void ExecutePositionOpenScript(PositionInfo position)
        {
            if (string.IsNullOrEmpty(_config.PositionOpenScript))
                return;

            if (!File.Exists(_config.PositionOpenScript))
            {
                _logger.Warning("Position Open Script not found: {ScriptPath}", _config.PositionOpenScript);
                return;
            }

            try
            {
                // Prepare script arguments: symbol and first fill timestamp
                var symbol = position.Contract.Symbol;
                var firstFillTime = position.FirstFillTimestamp?.ToString("yyyy-MM-dd HH:mm:ss") ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                
                var processInfo = new ProcessStartInfo
                {
                    FileName = _config.PositionOpenScript,
                    Arguments = $"\"{symbol}\" \"{firstFillTime}\"",
                    UseShellExecute = true,
                    CreateNoWindow = false
                };

                Process.Start(processInfo);
                _logger.Information("Position Open Script executed: {ScriptPath} with arguments: {Symbol} {FirstFillTime}", 
                    _config.PositionOpenScript, symbol, firstFillTime);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error executing Position Open Script: {ScriptPath}", _config.PositionOpenScript);
            }
        }

        private double RoundPriceForIB(double price)
        {
            // IB price rules: Under $1 = 4 decimal places, $1 and above = 2 decimal places
            if (price < 1.0)
            {
                return Math.Round(price, 4);
            }
            else
            {
                return Math.Round(price, 2);
            }
        }

        private string FormatPrice(double price)
        {
            // Dynamic formatting: show only necessary decimal places
            if (price < 1.0)
            {
                // For prices under $1, show up to 4 decimals but remove trailing zeros
                return price.ToString("0.####");
            }
            else
            {
                // For prices $1 and above, show up to 2 decimals but remove trailing zeros
                return price.ToString("0.##");
            }
        }

        private string GetPositionKey(Contract contract)
        {
            return $"{contract.Symbol}_{contract.SecType}_{contract.Currency}_{contract.Exchange}";
        }

        private string GetBarTimeString(Bar bar)
        {
            if (DateTime.TryParseExact(bar.Time, "yyyyMMdd-HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var barTime))
            {
                return barTime.ToString("HH:mm:ss");
            }
            return bar.Time;
        }

        public PositionInfo? GetPosition(string symbol)
        {
            lock (_lockObject)
            {
                return _positions.Values.FirstOrDefault(p => 
                    p.Contract.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
            }
        }

        public IEnumerable<PositionInfo> GetAllPositions()
        {
            lock (_lockObject)
            {
                return _positions.Values.ToList();
            }
        }

        private void OnIBConnected()
        {
            // Subscribe to market data for the configured symbol when connected
            SubscribeToMarketData();
        }

        private void OnTickPriceReceived(int tickerId, int field, double price, TickAttrib attribs)
        {
            // Only process ticks for our market data subscription
            if (tickerId != MARKET_DATA_TICKER_ID)
                return;

            if (string.IsNullOrEmpty(_config.Symbol))
                return;

            lock (_lockObject)
            {
                // Process ASK price ticks for buy orders
                if (field == TickType.ASK || field == TickType.DELAYED_ASK)
                {
                    var oldAskPrice = _currentAskPrice;
                    _currentAskPrice = price;

                    // Log significant ask price changes
                    if (Math.Abs(oldAskPrice - price) > 0.01 && oldAskPrice > 0)
                    {
                        _logger.Debug("Ask price updated for {Symbol}: {OldPrice:F2} -> {NewPrice:F2}", 
                            _config.Symbol, oldAskPrice, price);
                    }
                }
                // Process BID price ticks for sell orders
                else if (field == TickType.BID || field == TickType.DELAYED_BID)
                {
                    var oldBidPrice = _currentBidPrice;
                    _currentBidPrice = price;

                    // Log significant bid price changes
                    if (Math.Abs(oldBidPrice - price) > 0.01 && oldBidPrice > 0)
                    {
                        _logger.Debug("Bid price updated for {Symbol}: {OldPrice:F2} -> {NewPrice:F2}", 
                            _config.Symbol, oldBidPrice, price);
                    }
                }
                // Process LAST price ticks (current market price) for existing positions
                else if (field == TickType.LAST || field == TickType.DELAYED_LAST)
                {
                    var position = GetPosition(_config.Symbol);
                    if (position != null && position.IsLongPosition)
                    {
                        var oldPrice = position.MarketPrice;
                        position.MarketPrice = price;

                        // Log significant price changes
                        if (Math.Abs(oldPrice - price) > 0.01 && oldPrice > 0)
                        {
                            _logger.Debug("Market price updated for {Symbol}: {OldPrice:F2} -> {NewPrice:F2}", 
                                _config.Symbol, oldPrice, price);
                        }

                        // Check take-profit trigger first (higher priority than break-even)
                        if (position.TakeProfitActive && position.TakeProfitPrice.HasValue && price >= position.TakeProfitPrice.Value)
                        {
                            // Trigger take-profit close
                            TriggerTakeProfitClose(position, price);
                        }
                        else
                        {
                            // Check break-even trigger on every price update (only if take-profit not triggered)
                            CheckBreakEvenTrigger(position);
                        }
                    }
                }
            }
        }

        private void SubscribeToMarketData()
        {
            if (string.IsNullOrEmpty(_config.Symbol))
            {
                _logger.Debug("No symbol configured for market data subscription");
                return;
            }

            try
            {
                var contract = new Contract
                {
                    Symbol = _config.Symbol,
                    SecType = "STK",
                    Currency = "USD",
                    Exchange = "SMART"
                };

                _marketDataTickerId = MARKET_DATA_TICKER_ID;
                _ibService.RequestMarketData(_marketDataTickerId, contract);
                _logger.Information("Subscribed to market data for symbol {Symbol} with tickerId {TickerId}", 
                    _config.Symbol, _marketDataTickerId);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error subscribing to market data for symbol {Symbol}", _config.Symbol);
            }
        }

        private void UnsubscribeFromMarketData()
        {
            if (_marketDataTickerId > 0)
            {
                try
                {
                    _ibService.CancelMarketData(_marketDataTickerId);
                    _logger.Information("Cancelled market data subscription for tickerId {TickerId}", _marketDataTickerId);
                    _marketDataTickerId = -1;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error cancelling market data subscription");
                }
            }
        }

        public void ForceBreakEven(string symbol)
        {
            lock (_lockObject)
            {
                var position = GetPosition(symbol);
                if (position != null && position.IsLongPosition && !position.BreakEvenTriggered)
                {
                    // For force command, only require market price to be above average price
                    if (position.MarketPrice > position.AveragePrice)
                    {
                        _logger.Information("Break-Even manually triggered for {Symbol}. Market: {MarketPrice:F2}, Average: {AveragePrice:F2}", 
                            symbol, position.MarketPrice, position.AveragePrice);
                        CreateBreakEvenOrder(position);
                        position.BreakEvenTriggered = true;
                    }
                    else
                    {
                        _logger.Warning("Break-Even force rejected for {Symbol}. Market price {MarketPrice:F2} must be above average price {AveragePrice:F2}. Current difference: {Difference:F2}", 
                            symbol, position.MarketPrice, position.AveragePrice, position.MarketPrice - position.AveragePrice);
                    }
                }
            }
        }

        public void UpdateStopLoss(string symbol, double newStopLoss)
        {
            lock (_lockObject)
            {
                var position = GetPosition(symbol);
                if (position != null && position.IsLongPosition)
                {
                    _logger.Information("Stop-Loss manually updated for {Symbol}: {NewStopLoss}", symbol, newStopLoss);
                    _config.StopLoss = newStopLoss;
                    UpdateStopLossOrder(position);
                }
            }
        }

        public void UpdateSymbol(string newSymbol)
        {
            if (_config.Symbol == newSymbol)
                return;

            var oldSymbol = _config.Symbol;

            // Unsubscribe from old symbol
            UnsubscribeFromMarketData();

            // Update real-time bars subscription to new symbol
            _realTimeBarService.UpdateSymbol(newSymbol);

            // Clear bar history for old symbol
            if (!string.IsNullOrEmpty(oldSymbol))
            {
                _barTrailingStopManager.ClearHistory(oldSymbol);
            }

            // Reset ask and bid prices when changing symbols
            lock (_lockObject)
            {
                _currentAskPrice = 0.0;
                _currentBidPrice = 0.0;
                
                // Reset take-profit trigger when changing symbols
                ResetTakeProfitTrigger();
            }

            // Update configuration
            _config.Symbol = newSymbol;

            // Subscribe to new symbol if connected
            if (_ibService.IsConnected)
            {
                SubscribeToMarketData();
            }

            _logger.Information("Symbol updated from {OldSymbol} to {NewSymbol}, market data and real-time bars subscription refreshed", 
                oldSymbol ?? "none", newSymbol);
        }

        public double GetCurrentAskPrice(string symbol)
        {
            if (string.IsNullOrEmpty(symbol) || !symbol.Equals(_config.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning("Ask price requested for symbol {Symbol} but current symbol is {CurrentSymbol}", 
                    symbol, _config.Symbol);
                return 0.0;
            }

            lock (_lockObject)
            {
                if (_currentAskPrice <= 0)
                {
                    _logger.Warning("No ask price available for {Symbol}. Market data may not be subscribed or available.", symbol);
                }
                return _currentAskPrice;
            }
        }

        public async Task<string> CloseAllPositionsAndSetSellOrder()
        {
            var results = new List<string>();
            
            lock (_lockObject)
            {
                // Activate close mode to prevent new stop-loss orders during close process
                _isClosing = true;
                _logger.Information("Close mode activated - preventing new stop-loss orders during close process");
                
                // 1. Cancel all existing orders
                var cancelledOrders = new List<int>();
                foreach (var position in _positions.Values)
                {
                    if (position.StopLossOrderId.HasValue)
                    {
                        _ibService.CancelOrder(position.StopLossOrderId.Value);
                        cancelledOrders.Add(position.StopLossOrderId.Value);
                        _orderToPositionMap.Remove(position.StopLossOrderId.Value);
                        position.StopLossOrderId = null;
                        position.StopLossPrice = null;
                        position.StopLimitPrice = null;
                    }
                    
                    if (position.BreakEvenOrderId.HasValue && position.BreakEvenOrderId != position.StopLossOrderId)
                    {
                        _ibService.CancelOrder(position.BreakEvenOrderId.Value);
                        cancelledOrders.Add(position.BreakEvenOrderId.Value);
                        _orderToPositionMap.Remove(position.BreakEvenOrderId.Value);
                        position.BreakEvenOrderId = null;
                        position.BreakEvenTriggerPrice = null;
                    }
                }
                
                if (cancelledOrders.Any())
                {
                    results.Add($"Cancelled {cancelledOrders.Count} existing orders: {string.Join(", ", cancelledOrders)}");
                }

                // Check if all positions are already flat and reset close mode immediately
                if (_positions.Values.All(p => p.IsFlat))
                {
                    _isClosing = false;
                    _logger.Information("Close mode deactivated immediately - all positions are already flat");
                    results.Add("All positions are already closed. Close mode deactivated.");
                    return string.Join("\n", results);
                }
            }

            // 2. Set sell limit order at bid - market offset to close all long positions
            if (!string.IsNullOrEmpty(_config.Symbol))
            {
                try
                {
                    var bidPrice = GetCurrentBidPrice(_config.Symbol);
                    if (bidPrice > 0)
                    {
                        // Calculate total long position quantity to close
                        var totalLongQuantity = 0m;
                        lock (_lockObject)
                        {
                            foreach (var position in _positions.Values.Where(p => p.Quantity > 0))
                            {
                                totalLongQuantity += position.Quantity;
                            }
                        }

                        if (totalLongQuantity > 0)
                        {
                            var sellOffset = _config.GetSellOffsetValue(bidPrice);
                            var sellLimitPrice = RoundPriceForIB(bidPrice - sellOffset);
                            
                            var contract = CreateContract(_config.Symbol);
                            var sellOrder = CreateSellLimitOrder(totalLongQuantity, sellLimitPrice);
                            var orderId = _ibService.GetNextOrderId();
                            
                            _ibService.PlaceOrder(orderId, contract, sellOrder);
                            
                            results.Add($"Sell limit order placed to close {totalLongQuantity} shares at ${FormatPrice(sellLimitPrice)} (Bid: ${FormatPrice(bidPrice)} - SellOffset: ${FormatPrice(sellOffset)}, OrderId: {orderId})");
                            
                            _logger.Information("Sell limit order placed to close positions: {Symbol} OrderId:{OrderId} Qty:{Quantity} Price:{Price}", 
                                _config.Symbol, orderId, totalLongQuantity, sellLimitPrice);
                        }
                        else
                        {
                            results.Add("No long positions found to close.");
                        }
                    }
                    else
                    {
                        results.Add("Unable to get current bid price for sell limit order. Market data may not be available.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error placing sell limit order");
                    results.Add($"Error placing sell limit order: {ex.Message}");
                }
            }
            else
            {
                results.Add("No symbol configured for sell limit order.");
            }

            return string.Join("\n", results);
        }

        private Order CreateMarketCloseOrder(PositionInfo position)
        {
            var action = position.Quantity > 0 ? "SELL" : "BUY";
            var quantity = Math.Abs(position.Quantity);
            
            return new Order
            {
                Action = action,
                OrderType = "MKT",
                TotalQuantity = quantity,
                Tif = "GTC",
                Transmit = true,
                OutsideRth = true
            };
        }

        private Order CreateSellLimitOrder(decimal quantity, double limitPrice)
        {
            return new Order
            {
                Action = "SELL",
                OrderType = "LMT",
                TotalQuantity = quantity,
                LmtPrice = limitPrice,
                Tif = "GTC",
                Transmit = true,
                OutsideRth = true
            };
        }

        private Contract CreateContract(string symbol)
        {
            return new Contract
            {
                Symbol = symbol,
                SecType = "STK",
                Currency = "USD",
                Exchange = "SMART",
                PrimaryExch = ""
            };
        }

        private double _currentBidPrice = 0.0;

        public double GetCurrentBidPrice(string symbol)
        {
            if (string.IsNullOrEmpty(symbol) || !symbol.Equals(_config.Symbol, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning("Bid price requested for symbol {Symbol} but current symbol is {CurrentSymbol}", 
                    symbol, _config.Symbol);
                return 0.0;
            }

            lock (_lockObject)
            {
                if (_currentBidPrice <= 0)
                {
                    _logger.Warning("No bid price available for {Symbol}. Market data may not be subscribed or available.", symbol);
                }
                return _currentBidPrice;
            }
        }

        public async Task<string> ProcessSellOrder(decimal quantity, double? limitPrice = null)
        {
            try
            {
                if (string.IsNullOrEmpty(_config.Symbol))
                    return "No symbol configured for sell order.";

                // Check current position to prevent going short
                var currentPosition = GetPosition(_config.Symbol);
                var currentQuantity = currentPosition?.Quantity ?? 0m;
                
                if (currentQuantity <= 0)
                {
                    return $"Sell order rejected: No long position found for {_config.Symbol}. Current position: {currentQuantity}";
                }
                
                // Limit sell quantity to available shares to prevent going short
                var actualQuantity = Math.Min(quantity, currentQuantity);
                var wasLimited = actualQuantity < quantity;
                
                // CRITICAL: Adjust stop-loss order FIRST to prevent IB from trying to borrow shares
                if (currentPosition != null && currentPosition.StopLossOrderId.HasValue)
                {
                    var newStopLossQuantity = currentQuantity - actualQuantity;
                    if (newStopLossQuantity > 0)
                    {
                        // Reduce stop-loss order quantity immediately
                        var tempPosition = new PositionInfo
                        {
                            Contract = currentPosition.Contract,
                            Quantity = newStopLossQuantity,
                            StopLossOrderId = currentPosition.StopLossOrderId,
                            StopLossPrice = currentPosition.StopLossPrice,
                            StopLimitPrice = currentPosition.StopLimitPrice
                        };
                        ModifyStopLossOrderQuantity(tempPosition);
                        _logger.Information("Stop-Loss order quantity reduced BEFORE sell order: {Symbol} OrderId:{OrderId} NewQty:{NewQty}", 
                            _config.Symbol, currentPosition.StopLossOrderId.Value, newStopLossQuantity);
                    }
                    else
                    {
                        // Cancel stop-loss order completely if selling entire position
                        _ibService.CancelOrder(currentPosition.StopLossOrderId.Value);
                        _orderToPositionMap.Remove(currentPosition.StopLossOrderId.Value);
                        _logger.Information("Stop-Loss order cancelled BEFORE sell order: {Symbol} OrderId:{OrderId} (selling entire position)", 
                            _config.Symbol, currentPosition.StopLossOrderId.Value);
                    }
                }
                
                var contract = CreateContract(_config.Symbol);
                var orderId = _ibService.GetNextOrderId();

                string limitWarning = "";
                
                if (wasLimited)
                {
                    limitWarning = $" (Limited from {quantity} to {actualQuantity} shares to prevent short position)";
                    _logger.Warning("Sell order quantity limited: Requested {RequestedQty}, Available {AvailableQty}, Selling {ActualQty} for {Symbol}", 
                        quantity, currentQuantity, actualQuantity, _config.Symbol);
                }

                // Market-based sell order with bid - offset (explicit prices disabled for IB safety)
                var bidPrice = GetCurrentBidPrice(_config.Symbol);
                if (bidPrice <= 0)
                {
                    return "Unable to get current bid price. Market data may not be available.";
                }

                var sellOffset = _config.GetSellOffsetValue(bidPrice);
                var calculatedLimitPrice = RoundPriceForIB(bidPrice - sellOffset);
                
                var sellOrder = CreateSellLimitOrder(actualQuantity, calculatedLimitPrice);
                var orderDescription = $"Sell Limit: {actualQuantity} shares at ${FormatPrice(calculatedLimitPrice)} (Bid: ${FormatPrice(bidPrice)} - SellOffset: ${FormatPrice(sellOffset)}){limitWarning}";

                // Track this sell order to prevent stop-loss interference during execution
                lock (_lockObject)
                {
                    _activeSellOrderIds.Add(orderId);
                    _logger.Debug("Added sell order {OrderId} to active tracking for {Symbol}", orderId, _config.Symbol);
                }

                // Place sell order IMMEDIATELY after stop-loss adjustment
                _ibService.PlaceOrder(orderId, contract, sellOrder);

                _logger.Information("Sell order placed: {Symbol} OrderId:{OrderId} {Description}", 
                    _config.Symbol, orderId, orderDescription);

                return $"Sell order placed: {orderDescription} (OrderId: {orderId})";
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error placing sell order");
                return $"Error placing sell order: {ex.Message}";
            }
        }

        public bool IsClosing
        {
            get
            {
                lock (_lockObject)
                {
                    return _isClosing;
                }
            }
        }

        private void OnRealTimeBarReceived(int reqId, long date, double open, double high, double low, double close, decimal volume, decimal wap, int count)
        {
            // Forward to RealTimeBarService for processing
            _realTimeBarService.OnRealTimeBar(reqId, date, open, high, low, close, volume, wap, count);
        }

        private void OnRealTimeBarFromService(int tickerId, Bar bar)
        {
            if (!_config.UseBarBasedTrailing || string.IsNullOrEmpty(_config.Symbol))
                return;

            if (_config.BarDebug)
            {
                _logger.Information("BAR FORWARDED TO MANAGER: {Symbol} {Time} O:{Open:F2} H:{High:F2} L:{Low:F2} C:{Close:F2}", 
                    _config.Symbol, GetBarTimeString(bar), bar.Open, bar.High, bar.Low, bar.Close);
            }

            lock (_lockObject)
            {
                var position = GetPosition(_config.Symbol);
                if (position != null && position.IsLongPosition && position.StopLossOrderId.HasValue)
                {
                    // Only process trailing if we have an active stop-loss order
                    var newStopPrice = _barTrailingStopManager.ProcessNewBar(bar, position);
                    
                    if (newStopPrice.HasValue)
                    {
                        // Update the stop-loss order with the new trailing stop price
                        var limitOffset = _config.GetSellOffsetValue(newStopPrice.Value);
                        var limitPrice = RoundPriceForIB(newStopPrice.Value - limitOffset);
                        
                        ModifyStopLossOrder(position, RoundPriceForIB(newStopPrice.Value), limitPrice);
                        
                        _logger.Information("Bar-based trailing stop updated for {Symbol}: New Stop:{NewStop:F2} Limit:{Limit:F2} based on bar Close:{Close:F2}", 
                            _config.Symbol, newStopPrice.Value, limitPrice, bar.Close);
                    }
                }
                else if (position != null && position.IsLongPosition && !position.StopLossOrderId.HasValue)
                {
                    _logger.Debug("Bar-based trailing skipped for {Symbol} - no active stop-loss order (Bar Close:{Close:F2})", 
                        _config.Symbol, bar.Close);
                }
            }
        }

        public string GetBarTrailingStatus()
        {
            if (!_config.UseBarBasedTrailing)
                return "Bar-based trailing is disabled";

            if (string.IsNullOrEmpty(_config.Symbol))
                return "No symbol configured for bar-based trailing";

            return _barTrailingStopManager.GetTrailingStopStatus(_config.Symbol);
        }

        public string SetTakeProfitTrigger(string symbol, double targetPrice)
        {
            lock (_lockObject)
            {
                var position = GetPosition(symbol);
                
                if (position == null || position.IsFlat)
                {
                    // No active position - set flat state take-profit target
                    // Reset any existing take-profit trigger first
                    ResetTakeProfitTrigger();
                    
                    // Set flat state target
                    _flatStateTakeProfitPrice = targetPrice;
                    _flatStateTakeProfitActive = true;
                    
                    _logger.Information("Take-profit trigger set for flat state {Symbol}: Target price {TargetPrice:F2}", symbol, targetPrice);
                    return $"Take-profit trigger set for {symbol} at ${targetPrice:F2}. Target will be activated when a new position is opened.";
                }
                else
                {
                    // Active position - set take-profit trigger immediately
                    // Reset any existing take-profit trigger first
                    ResetTakeProfitTrigger();

                    // Set new take-profit trigger
                    position.TakeProfitPrice = targetPrice;
                    position.TakeProfitActive = true;

                    _logger.Information("Take-profit trigger set for {Symbol}: Target price {TargetPrice:F2}", symbol, targetPrice);
                    return $"Take-profit trigger set for {symbol} at ${targetPrice:F2}. Position will be closed when market price reaches this level.";
                }
            }
        }

        public void ResetTakeProfitTrigger()
        {
            lock (_lockObject)
            {
                var hadActiveTrigger = false;
                var hadFlatStateTrigger = false;
                
                // Reset position-based triggers
                foreach (var position in _positions.Values)
                {
                    if (position.TakeProfitActive)
                    {
                        hadActiveTrigger = true;
                        _logger.Information("Take-profit trigger reset for {Symbol} (was: {TargetPrice:F2})", 
                            position.Contract.Symbol, position.TakeProfitPrice ?? 0);
                    }
                    position.TakeProfitPrice = null;
                    position.TakeProfitActive = false;
                }
                
                // Reset flat state triggers
                if (_flatStateTakeProfitActive)
                {
                    hadFlatStateTrigger = true;
                    _logger.Information("Take-profit flat state trigger reset (was: {TargetPrice:F2})", 
                        _flatStateTakeProfitPrice ?? 0);
                }
                _flatStateTakeProfitPrice = null;
                _flatStateTakeProfitActive = false;
                
                if (hadActiveTrigger || hadFlatStateTrigger)
                {
                    _logger.Information("Take-profit trigger reset to default state");
                }
            }
        }

        public string GetFlatStateTakeProfitStatus()
        {
            lock (_lockObject)
            {
                if (_flatStateTakeProfitActive && _flatStateTakeProfitPrice.HasValue)
                {
                    return $"Flat state take-profit target: ${_flatStateTakeProfitPrice.Value:F2} (will activate when new position opens)";
                }
                return string.Empty;
            }
        }

        private async void TriggerTakeProfitClose(PositionInfo position, double currentPrice)
        {
            try
            {
                _logger.Information("Take-profit triggered for {Symbol} at price {CurrentPrice:F2} (target: {TargetPrice:F2})", 
                    position.Contract.Symbol, currentPrice, position.TakeProfitPrice ?? 0);

                // Reset take-profit trigger before executing close
                ResetTakeProfitTrigger();

                // Execute the same close routine as manual close command
                var result = await CloseAllPositionsAndSetSellOrder();
                _logger.Information("Take-profit close executed: {Result}", result);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error executing take-profit close for {Symbol}", position.Contract.Symbol);
            }
        }

        public void Dispose()
        {
            UnsubscribeFromMarketData();
            _realTimeBarService?.Dispose();
        }
    }
}
