using IBApi;
using Serilog;
using Serilog.Events;
using IBMonitor.Config;

namespace IBMonitor.Services
{
    public class IBConnectionService : DefaultEWrapper
    {
        private readonly ILogger _logger;
        private readonly MonitorConfig _config;
        private EClientSocket? _clientSocket;
        private EReader? _reader;
        private readonly EReaderMonitorSignal _signal;
        private bool _isConnected;
        private bool _isConnecting;
        private bool _isShuttingDown;
        private Timer? _reconnectTimer;
        private const int ReconnectIntervalMs = 5000;

        public event Action? Connected;
        public event Action? Disconnected;
        public event Action<int>? NextValidIdReceived;
        public event Action<string, Contract, decimal, double>? PositionUpdate;
        public event Action<int, string, decimal, decimal, double, int, int, double, int, string, double>? OrderStatusUpdate;
        public event Action<int, Contract, Order, OrderState>? OpenOrderReceived;
        public event Action<int, int, string, string>? ErrorReceived;
        public event Action<int, int, double, TickAttrib>? TickPriceReceived;

        public int NextOrderId { get; private set; } = 1;
        public bool IsConnected => _isConnected;

        public int GetNextOrderId()
        {
            return NextOrderId++;
        }

        public IBConnectionService(ILogger logger, MonitorConfig config)
        {
            _logger = logger;
            _config = config;
            _signal = new EReaderMonitorSignal();
        }

        public async Task<bool> ConnectAsync()
        {
            if (_isConnected || _isConnecting)
                return _isConnected;

            _isConnecting = true;
            
            try
            {
                _logger.Information("Connecting to IB Gateway/TWS on port {Port}...", _config.Port);
                
                _clientSocket = new EClientSocket(this, _signal);
                _clientSocket.eConnect("127.0.0.1", _config.Port, _config.ClientId);

                // Wait for connection acknowledgment
                await Task.Delay(1000);

                if (_clientSocket.IsConnected())
                {
                    _logger.Information("Successfully connected to IB");
                    _isConnected = true;
                    _isConnecting = false;

                    // Start reader thread
                    _reader = new EReader(_clientSocket, _signal);
                    _reader.Start();

                    // Start processing messages
                    _ = Task.Run(ProcessMessages);

                    // Request account updates and positions
                    RequestInitialData();

                    Connected?.Invoke();
                    return true;
                }
                else
                {
                    _logger.Error("Failed to connect to IB");
                    _isConnecting = false;
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error connecting to IB");
                _isConnecting = false;
                return false;
            }
        }

        public void Disconnect()
        {
            _isShuttingDown = true;
            _reconnectTimer?.Dispose();
            _reconnectTimer = null;

            if (_clientSocket?.IsConnected() == true)
            {
                _clientSocket.eDisconnect();
            }

            _isConnected = false;
            _logger.Information("Disconnected from IB");
            Disconnected?.Invoke();
        }

        private void ProcessMessages()
        {
            while (_isConnected && _clientSocket?.IsConnected() == true && !_isShuttingDown)
            {
                try
                {
                    _signal.waitForSignal();
                    _reader?.processMsgs();
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error processing IB messages");
                    break;
                }
            }
        }

        private void RequestInitialData()
        {
            try
            {
                // Request next valid order ID
                _clientSocket?.reqIds(-1);

                // Request positions for all accounts
                _clientSocket?.reqPositions();

                // Request open orders
                _clientSocket?.reqAllOpenOrders();

                _logger.Debug("Initial data requests sent");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error requesting initial data");
            }
        }

        private void StartReconnectTimer()
        {
            if (_isShuttingDown)
                return;
                
            _reconnectTimer?.Dispose();
            _reconnectTimer = new Timer(async _ =>
            {
                if (!_isConnected && !_isConnecting && !_isShuttingDown)
                {
                    _logger.Information("Attempting to reconnect to IB...");
                    await ConnectAsync();
                }
            }, null, ReconnectIntervalMs, ReconnectIntervalMs);
        }

        public void PlaceOrder(int orderId, Contract contract, Order order)
        {
            if (!_isConnected || _clientSocket == null)
            {
                _logger.Warning("Cannot place order - not connected");
                return;
            }

            try
            {
                _clientSocket.placeOrder(orderId, contract, order);
                _logger.Debug("Order {OrderId} placed for {Symbol}", orderId, contract.Symbol);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error placing order {OrderId}", orderId);
            }
        }

        public void CancelOrder(int orderId)
        {
            if (!_isConnected || _clientSocket == null)
            {
                _logger.Warning("Cannot cancel order - not connected");
                return;
            }

            try
            {
                _clientSocket.cancelOrder(orderId, new OrderCancel());
                _logger.Debug("Order {OrderId} cancelled", orderId);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error cancelling order {OrderId}", orderId);
            }
        }

        public void RequestMarketData(int tickerId, Contract contract)
        {
            if (!_isConnected || _clientSocket == null)
            {
                _logger.Warning("Cannot request market data - not connected");
                return;
            }

            try
            {
                _clientSocket.reqMktData(tickerId, contract, "", false, false, new List<TagValue>());
                _logger.Debug("Market data requested for {Symbol} with tickerId {TickerId}", contract.Symbol, tickerId);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error requesting market data for {Symbol}", contract.Symbol);
            }
        }

        public void CancelMarketData(int tickerId)
        {
            if (!_isConnected || _clientSocket == null)
            {
                _logger.Warning("Cannot cancel market data - not connected");
                return;
            }

            try
            {
                _clientSocket.cancelMktData(tickerId);
                _logger.Debug("Market data cancelled for tickerId {TickerId}", tickerId);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error cancelling market data for tickerId {TickerId}", tickerId);
            }
        }

        // EWrapper implementations
        public override void nextValidId(int orderId)
        {
            NextOrderId = orderId;
            _logger.Debug("Next valid order ID: {OrderId}", orderId);
            NextValidIdReceived?.Invoke(orderId);
        }

        public override void position(string account, Contract contract, decimal pos, double avgCost)
        {
            _logger.Debug("Position Update: {Account} {Symbol} Qty:{Quantity} AvgCost:{AvgCost}", 
                account, contract.Symbol, pos, avgCost);
            PositionUpdate?.Invoke(account, contract, pos, avgCost);
        }

        public override void orderStatus(int orderId, string status, decimal filled, decimal remaining, 
            double avgFillPrice, int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice)
        {
            _logger.Debug("Order Status: {OrderId} Status:{Status} Filled:{Filled} Remaining:{Remaining}", 
                orderId, status, filled, remaining);
            OrderStatusUpdate?.Invoke(orderId, status, filled, remaining, avgFillPrice, permId, parentId, lastFillPrice, clientId, whyHeld, mktCapPrice);
        }

        public override void openOrder(int orderId, Contract contract, Order order, OrderState orderState)
        {
            _logger.Debug("Open Order: {OrderId} {Symbol} {OrderType} {TotalQuantity}", 
                orderId, contract.Symbol, order.OrderType, order.TotalQuantity);
            OpenOrderReceived?.Invoke(orderId, contract, order, orderState);
        }

        public override void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
        {
            try
            {
                TickPriceReceived?.Invoke(tickerId, field, price, attribs);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error processing tick price for tickerId {TickerId}", tickerId);
            }
        }

        public override void error(int id, int errorCode, string errorMsg, string advancedOrderRejectJson)
        {
            var logLevel = errorCode == 2104 || errorCode == 2106 || errorCode == 2158 ? 
                LogEventLevel.Debug : LogEventLevel.Warning;
            
            _logger.Write(logLevel, "IB Error: ID:{Id} Code:{ErrorCode} Message:{ErrorMessage}", 
                id, errorCode, errorMsg);
            
            ErrorReceived?.Invoke(id, errorCode, errorMsg, advancedOrderRejectJson);
        }

        public override void connectionClosed()
        {
            _isConnected = false;
            _logger.Warning("IB connection closed");
            Disconnected?.Invoke();
            
            // Only start reconnect timer if we're not shutting down
            if (!_isShuttingDown)
            {
                StartReconnectTimer();
            }
        }

        public void Dispose()
        {
            Disconnect();
            _reconnectTimer?.Dispose();
        }
    }
} 