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
        private readonly Dictionary<string, PositionInfo> _positions = new();
        private readonly Dictionary<int, int> _orderToPositionMap = new(); // OrderId -> Position tracking
        private bool _firstPositionDetected = false;
        private readonly object _lockObject = new object();
        private int _marketDataTickerId = -1;
        private const int MARKET_DATA_TICKER_ID = 1000; // Fixed ticker ID for symbol market data

        public event Action<PositionInfo>? PositionOpened;
        public event Action<PositionInfo>? PositionClosed;
        public event Action<PositionInfo>? PositionChanged;

        public PositionMonitorService(ILogger logger, MonitorConfig config, IBConnectionService ibService)
        {
            _logger = logger;
            _config = config;
            _ibService = ibService;

            // Subscribe to IB events
            _ibService.PositionUpdate += OnPositionUpdate;
            _ibService.OrderStatusUpdate += OnOrderStatusUpdate;
            _ibService.OpenOrderReceived += OnOpenOrderReceived;
            _ibService.TickPriceReceived += OnTickPriceReceived;
            _ibService.Connected += OnIBConnected;
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
                    _logger.Information("New position detected: {Position}", positionInfo);
                    
                    // Execute position open script if configured
                    ExecutePositionOpenScript(positionInfo);
                    
                    // Create stop-loss order for long positions only
                    if (position > 0)
                    {
                        CreateStopLossOrder(positionInfo);
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
                                        _logger.Information("Average price changed from {OldPrice:F2} to {NewPrice:F2} for {Symbol}",
                            existingPosition.AveragePrice, avgCost, contract.Symbol);
                        existingPosition.AveragePrice = avgCost;
                        
                        // Update stop-loss orders immediately on average price change
                        if (position > 0)
                        {
                            UpdateStopLossOrder(existingPosition);
                        }
                    }

                    if (isNowFlat && !wasFlat)
                    {
                        // Position closed
                        _logger.Information("Position closed: {Symbol}", contract.Symbol);
                        CancelExistingOrders(existingPosition);
                        PositionClosed?.Invoke(existingPosition);
                    }
                    else                     if (positionSizeChanged && !isNowFlat)
                    {
                        // Position size changed but still open
                        _logger.Information("Position size changed: {Symbol} {OldQty} -> {NewQty}", 
                            contract.Symbol, existingPosition.Quantity, position);
                        
                        if (position > 0) // Only for long positions
                        {
                            // Only modify quantity, don't recreate the order
                            ModifyStopLossOrderQuantity(existingPosition);
                            CheckBreakEvenTrigger(existingPosition);
                        }
                        
                        PositionChanged?.Invoke(existingPosition);
                    }
                }
            }
        }

        private void CreateStopLossOrder(PositionInfo position)
        {
            if (position.Quantity <= 0) return; // Only for long positions

            var stopPrice = position.AveragePrice - _config.StopLoss;
            var limitOffset = _config.GetMarketOffsetValue(stopPrice);
            var limitPrice = stopPrice - limitOffset;

            var stopOrder = CreateStopLimitOrder(position.Contract, position.Quantity, stopPrice, limitPrice);
            var orderId = _ibService.GetNextOrderId();

            _ibService.PlaceOrder(orderId, position.Contract, stopOrder);

            position.StopLossOrderId = orderId;
            position.StopLossPrice = stopPrice;
            position.StopLimitPrice = limitPrice;

            _orderToPositionMap[orderId] = GetPositionKey(position.Contract).GetHashCode();

            _logger.Information("Stop-Loss order created: {Symbol} OrderId:{OrderId} Stop:{StopPrice:F2} Limit:{LimitPrice:F2}", 
                position.Contract.Symbol, orderId, stopPrice, limitPrice);
        }

        private void UpdateStopLossOrder(PositionInfo position)
        {
            if (position.Quantity <= 0) return; // Only for long positions

            // Calculate new stop prices based on updated average price
            var stopPrice = position.AveragePrice - _config.StopLoss;
            var limitOffset = _config.GetMarketOffsetValue(stopPrice);
            var limitPrice = stopPrice - limitOffset;

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

            _logger.Information("Stop-Loss order modified: {Symbol} OrderId:{OrderId} Qty:{Quantity} Stop:{StopPrice:F2} Limit:{LimitPrice:F2}", 
                position.Contract.Symbol, position.StopLossOrderId.Value, position.Quantity, stopPrice, limitPrice);
        }

        private void CheckBreakEvenTrigger(PositionInfo position)
        {
            if (position.Quantity <= 0 || !_config.BreakEven.HasValue || position.BreakEvenTriggered) 
                return;

            var currentProfit = (position.MarketPrice - position.AveragePrice) * (double)position.Quantity;
            
            if (currentProfit >= _config.BreakEven.Value)
            {
                _logger.Information("Break-Even threshold reached for {Symbol}. Profit: {Profit:F2}, Threshold: {Threshold:F2}", 
                    position.Contract.Symbol, currentProfit, _config.BreakEven.Value);
                
                CreateBreakEvenOrder(position);
                position.BreakEvenTriggered = true;
            }
        }

        private void CreateBreakEvenOrder(PositionInfo position)
        {
            if (position.Quantity <= 0) return; // Only for long positions

            var breakEvenPrice = position.AveragePrice + _config.BreakEvenOffset;
            var limitOffset = _config.GetMarketOffsetValue(breakEvenPrice);
            var limitPrice = breakEvenPrice - limitOffset;

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

            _logger.Information("Break-Even order activated: {Symbol} OrderId:{OrderId} Stop:{StopPrice:F2} Limit:{LimitPrice:F2}", 
                position.Contract.Symbol, position.StopLossOrderId.Value, breakEvenPrice, limitPrice);
        }

        private Order CreateStopLimitOrder(Contract contract, decimal quantity, double stopPrice, double limitPrice)
        {
            return new Order
            {
                Action = "SELL", // Only for long positions
                OrderType = "STP LMT",
                TotalQuantity = quantity,
                AuxPrice = stopPrice,
                LmtPrice = limitPrice,
                Tif = "GTC",
                Transmit = true
            };
        }

        private void CancelExistingOrders(PositionInfo position)
        {
            if (position.StopLossOrderId.HasValue)
            {
                _ibService.CancelOrder(position.StopLossOrderId.Value);
                _orderToPositionMap.Remove(position.StopLossOrderId.Value);
                position.StopLossOrderId = null;
            }

            if (position.BreakEvenOrderId.HasValue)
            {
                _ibService.CancelOrder(position.BreakEvenOrderId.Value);
                _orderToPositionMap.Remove(position.BreakEvenOrderId.Value);
                position.BreakEvenOrderId = null;
            }
        }

        private void OnOrderStatusUpdate(int orderId, string status, decimal filled, decimal remaining, 
            double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice)
        {
            if (_orderToPositionMap.ContainsKey(orderId))
            {
                _logger.Debug("Stop-Loss order update: {OrderId} Status:{Status} Filled:{Filled}", 
                    orderId, status, filled);

                if (status == "Filled" || status == "Cancelled")
                {
                    _orderToPositionMap.Remove(orderId);
                }
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
            if (string.IsNullOrEmpty(_config.PositionOpenScript) || _firstPositionDetected)
                return;

            _firstPositionDetected = true;

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

        private string GetPositionKey(Contract contract)
        {
            return $"{contract.Symbol}_{contract.SecType}_{contract.Currency}_{contract.Exchange}";
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

            // Only process LAST price ticks (current market price)
            if (field != TickType.LAST && field != TickType.DELAYED_LAST)
                return;

            if (string.IsNullOrEmpty(_config.Symbol))
                return;

            lock (_lockObject)
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

                    // Check break-even trigger on every price update
                    CheckBreakEvenTrigger(position);
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
                    _logger.Information("Break-Even manually triggered for {Symbol}", symbol);
                    CreateBreakEvenOrder(position);
                    position.BreakEvenTriggered = true;
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

            // Unsubscribe from old symbol
            UnsubscribeFromMarketData();

            // Update configuration
            _config.Symbol = newSymbol;

            // Subscribe to new symbol if connected
            if (_ibService.IsConnected)
            {
                SubscribeToMarketData();
            }

            _logger.Information("Symbol updated to {Symbol}, market data subscription refreshed", newSymbol);
        }

        public void Dispose()
        {
            UnsubscribeFromMarketData();
        }
    }
} 