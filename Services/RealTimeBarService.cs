using IBApi;
using Serilog;
using IBMonitor.Config;
using System.Collections.Concurrent;

namespace IBMonitor.Services
{
    public class RealTimeBarService
    {
        private readonly ILogger _logger;
        private readonly MonitorConfig _config;
        private readonly IBConnectionService _ibService;
        private readonly BarAggregatorService _barAggregator;
        private readonly ConcurrentDictionary<int, string> _activeSubscriptions = new();
        private int _nextTickerId = 2000; // Start from 2000 to avoid conflicts with market data

        public event Action<int, Bar>? RealTimeBarReceived;

        public RealTimeBarService(ILogger logger, MonitorConfig config, IBConnectionService ibService)
        {
            _logger = logger;
            _config = config;
            _ibService = ibService;

            // Initialize bar aggregator for 5s -> 10s conversion
            _barAggregator = new BarAggregatorService(_logger, _config);
            _barAggregator.AggregatedBarReady += OnAggregatedBarReady;

            // Subscribe to IB connection events
            _ibService.Connected += OnIBConnected;
            _ibService.Disconnected += OnIBDisconnected;
        }

        private void OnIBConnected()
        {
            // Auto-subscribe to real-time bars if bar-based trailing is enabled
            if (_config.UseBarBasedTrailing && !string.IsNullOrEmpty(_config.Symbol))
            {
                SubscribeToRealTimeBars(_config.Symbol);
            }
        }

        private void OnIBDisconnected()
        {
            // Clear active subscriptions on disconnect
            _activeSubscriptions.Clear();
        }

        public int SubscribeToRealTimeBars(string symbol)
        {
            if (string.IsNullOrEmpty(symbol))
            {
                _logger.Warning("Cannot subscribe to real-time bars - symbol is empty");
                return -1;
            }

            if (!_ibService.IsConnected)
            {
                _logger.Warning("Cannot subscribe to real-time bars - not connected to IB");
                return -1;
            }

            try
            {
                var contract = CreateContract(symbol);
                var tickerId = _nextTickerId++;

                // Subscribe to real-time bars
                // Parameters: tickerId, contract, barSize (seconds), whatToShow, useRTH, realTimeBarsOptions
                _ibService.RequestRealTimeBars(tickerId, contract, _config.BarInterval, "TRADES", false);

                _activeSubscriptions[tickerId] = symbol;
                _logger.Information("Subscribed to {Interval}s real-time bars for {Symbol} with tickerId {TickerId}", 
                    _config.BarInterval, symbol, tickerId);

                return tickerId;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error subscribing to real-time bars for {Symbol}", symbol);
                return -1;
            }
        }

        public void UnsubscribeFromRealTimeBars(int tickerId)
        {
            if (!_activeSubscriptions.ContainsKey(tickerId))
            {
                _logger.Warning("Cannot unsubscribe from real-time bars - tickerId {TickerId} not found", tickerId);
                return;
            }

            if (!_ibService.IsConnected)
            {
                _logger.Warning("Cannot unsubscribe from real-time bars - not connected to IB");
                return;
            }

            try
            {
                var symbol = _activeSubscriptions[tickerId];
                _ibService.CancelRealTimeBars(tickerId);
                _activeSubscriptions.TryRemove(tickerId, out _);

                _logger.Information("Unsubscribed from real-time bars for {Symbol} with tickerId {TickerId}", 
                    symbol, tickerId);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error unsubscribing from real-time bars for tickerId {TickerId}", tickerId);
            }
        }

        public void UnsubscribeAll()
        {
            var tickerIds = _activeSubscriptions.Keys.ToList();
            foreach (var tickerId in tickerIds)
            {
                UnsubscribeFromRealTimeBars(tickerId);
            }
        }

        public void OnRealTimeBar(int reqId, long date, double open, double high, double low, double close, decimal volume, decimal wap, int count)
        {
            if (!_activeSubscriptions.ContainsKey(reqId))
            {
                _logger.Debug("Received real-time bar for unknown tickerId {TickerId}", reqId);
                return;
            }

            try
            {
                var symbol = _activeSubscriptions[reqId];
                var timestamp = UnixTimestampToDateTime(date);
                var bar = new Bar(timestamp.ToString("yyyyMMdd-HH:mm:ss"), open, high, low, close, volume, count, wap);

                if (_config.BarDebug)
                {
                    _logger.Information("RAW BAR RECEIVED: {Symbol} {Time} O:{Open:F2} H:{High:F2} L:{Low:F2} C:{Close:F2} V:{Volume} [5s from IB]", 
                        symbol, timestamp.ToString("HH:mm:ss"), open, high, low, close, volume);
                }
                else
                {
                    _logger.Debug("Real-time bar received for {Symbol}: {Time} O:{Open:F2} H:{High:F2} L:{Low:F2} C:{Close:F2} V:{Volume}", 
                        symbol, timestamp.ToString("HH:mm:ss"), open, high, low, close, volume);
                }

                // Send raw 5s bar to aggregator for 10s conversion
                _barAggregator.ProcessRawBar(reqId, bar, symbol);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error processing real-time bar for tickerId {TickerId}", reqId);
            }
        }

        private void OnAggregatedBarReady(int tickerId, Bar aggregatedBar)
        {
            // Forward aggregated 10s bar to subscribers
            RealTimeBarReceived?.Invoke(tickerId, aggregatedBar);
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

        private static DateTime UnixTimestampToDateTime(long unixTimestamp)
        {
            var unixBaseTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            return unixBaseTime.AddSeconds(unixTimestamp).ToLocalTime();
        }

        public void UpdateSymbol(string newSymbol)
        {
            // Clear aggregation state for old symbol
            if (!string.IsNullOrEmpty(_config.Symbol))
            {
                _barAggregator.ClearState(_config.Symbol);
            }

            // Unsubscribe from all current subscriptions
            UnsubscribeAll();

            // Subscribe to new symbol if bar-based trailing is enabled
            if (_config.UseBarBasedTrailing && !string.IsNullOrEmpty(newSymbol))
            {
                SubscribeToRealTimeBars(newSymbol);
            }
        }

        public bool IsSubscribed(string symbol)
        {
            return _activeSubscriptions.Values.Contains(symbol, StringComparer.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            UnsubscribeAll();
        }
    }
}
