using IBApi;
using Serilog;
using IBMonitor.Config;

namespace IBMonitor.Services
{
    public class BarAggregatorService
    {
        private readonly ILogger _logger;
        private readonly MonitorConfig _config;
        private readonly Dictionary<string, BarAggregationState> _aggregationStates = new();
        private readonly object _lockObject = new object();

        public event Action<int, Bar>? AggregatedBarReady;

        public BarAggregatorService(ILogger logger, MonitorConfig config)
        {
            _logger = logger;
            _config = config;
        }

        public void ProcessRawBar(int tickerId, Bar rawBar, string symbol)
        {
            lock (_lockObject)
            {
                // Initialize aggregation state for this symbol if needed
                if (!_aggregationStates.ContainsKey(symbol))
                {
                    _aggregationStates[symbol] = new BarAggregationState();
                }

                var state = _aggregationStates[symbol];
                var aggregatedBar = state.ProcessRawBar(rawBar, _config);

                if (aggregatedBar != null)
                {
                    if (_config.BarDebug)
                    {
                        _logger.Information("AGGREGATED BAR READY: {Symbol} {Time} O:{Open:F2} H:{High:F2} L:{Low:F2} C:{Close:F2} V:{Volume} [10s from 2x5s bars]", 
                            symbol, GetBarTimeString(aggregatedBar), aggregatedBar.Open, aggregatedBar.High, aggregatedBar.Low, aggregatedBar.Close, aggregatedBar.Volume);
                    }

                    // Forward aggregated bar to subscribers
                    AggregatedBarReady?.Invoke(tickerId, aggregatedBar);
                }
            }
        }

        public void ClearState(string symbol)
        {
            lock (_lockObject)
            {
                if (_aggregationStates.ContainsKey(symbol))
                {
                    _aggregationStates[symbol].Reset();
                    _logger.Debug("Cleared aggregation state for {Symbol}", symbol);
                }
            }
        }

        public void ClearAllStates()
        {
            lock (_lockObject)
            {
                foreach (var state in _aggregationStates.Values)
                {
                    state.Reset();
                }
                _aggregationStates.Clear();
                _logger.Debug("Cleared all aggregation states");
            }
        }

        private string GetBarTimeString(Bar bar)
        {
            if (DateTime.TryParseExact(bar.Time, "yyyyMMdd-HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var barTime))
            {
                return barTime.ToString("HH:mm:ss");
            }
            return bar.Time;
        }

        public string GetAggregationStatus(string symbol)
        {
            lock (_lockObject)
            {
                if (!_aggregationStates.ContainsKey(symbol))
                    return $"No aggregation state for {symbol}";

                var state = _aggregationStates[symbol];
                return state.GetStatus(symbol);
            }
        }
    }

    internal class BarAggregationState
    {
        private Bar? _pendingBar = null;
        private bool _isAligned = false;

        public Bar? ProcessRawBar(Bar rawBar, MonitorConfig config)
        {
            // Check if this 5s bar is aligned (ends on 0,10,20,30,40,50 seconds)
            if (!IsAligned(rawBar) && !_isAligned)
            {
                // Discard non-aligned bars until we get the first aligned bar
                if (config.BarDebug)
                {
                    var time = ParseBarTime(rawBar.Time);
                    if (time.HasValue)
                    {
                        // Note: Using Console.WriteLine for immediate debug output since we don't have logger access in this state class
                        Console.WriteLine($"DISCARDING NON-ALIGNED BAR: {time.Value:HH:mm:ss} (seconds: {time.Value.Second})");
                    }
                }
                return null;
            }

            // Mark as aligned once we receive the first aligned bar
            if (!_isAligned)
            {
                _isAligned = true;
                if (config.BarDebug)
                {
                    var time = ParseBarTime(rawBar.Time);
                    if (time.HasValue)
                    {
                        Console.WriteLine($"FIRST ALIGNED BAR RECEIVED: {time.Value:HH:mm:ss} (seconds: {time.Value.Second}) - Aggregation now active");
                    }
                }
            }

            if (_pendingBar == null)
            {
                // First bar of the pair - store it
                _pendingBar = rawBar;
                if (config.BarDebug)
                {
                    Console.WriteLine($"STORING FIRST BAR OF PAIR: {GetBarTimeString(rawBar)}");
                }
                return null;
            }
            else
            {
                // Second bar of the pair - aggregate and return
                var firstBarTime = GetBarTimeString(_pendingBar);
                var aggregatedBar = AggregateBars(_pendingBar, rawBar);
                _pendingBar = null; // Reset for next pair
                
                if (config.BarDebug)
                {
                    Console.WriteLine($"AGGREGATING PAIR: {firstBarTime} + {GetBarTimeString(rawBar)} → {GetBarTimeString(aggregatedBar)}");
                }
                
                return aggregatedBar;
            }
        }

        private bool IsAligned(Bar bar)
        {
            var time = ParseBarTime(bar.Time);
            if (!time.HasValue)
                return false;

            // Check if seconds are aligned to 10-second boundaries (0, 10, 20, 30, 40, 50)
            return time.Value.Second % 10 == 0;
        }

        private DateTime? ParseBarTime(string barTime)
        {
            if (DateTime.TryParseExact(barTime, "yyyyMMdd-HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var time))
            {
                return time;
            }
            return null;
        }

        private Bar AggregateBars(Bar first, Bar second)
        {
            // Use the end time of the second bar as the aggregated bar time
            var aggregatedTime = second.Time;
            
            // Aggregate OHLC values
            var open = first.Open;
            var high = Math.Max(first.High, second.High);
            var low = Math.Min(first.Low, second.Low);
            var close = second.Close;
            var volume = first.Volume + second.Volume;
            var count = first.Count + second.Count;
            
            // Calculate weighted average price (WAP)
            var totalValue = (first.WAP * first.Volume) + (second.WAP * second.Volume);
            var totalVolume = first.Volume + second.Volume;
            var wap = totalVolume > 0 ? totalValue / totalVolume : (first.WAP + second.WAP) / 2;

            return new Bar(aggregatedTime, open, high, low, close, volume, count, wap);
        }

        private string GetBarTimeString(Bar bar)
        {
            if (DateTime.TryParseExact(bar.Time, "yyyyMMdd-HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var barTime))
            {
                return barTime.ToString("HH:mm:ss");
            }
            return bar.Time;
        }

        public void Reset()
        {
            _pendingBar = null;
            _isAligned = false;
        }

        public string GetStatus(string symbol)
        {
            var status = $"{symbol}: Aligned={_isAligned}";
            if (_pendingBar != null)
            {
                status += $", Pending={GetBarTimeString(_pendingBar)}";
            }
            else
            {
                status += ", Pending=None";
            }
            return status;
        }
    }
}
