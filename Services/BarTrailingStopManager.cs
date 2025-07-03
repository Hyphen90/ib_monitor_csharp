using IBApi;
using Serilog;
using IBMonitor.Config;
using IBMonitor.Models;
using System.Collections.Concurrent;

namespace IBMonitor.Services
{
    public class BarTrailingStopManager
    {
        private readonly ILogger _logger;
        private readonly MonitorConfig _config;
        private readonly ConcurrentDictionary<string, Queue<Bar>> _barHistory = new();
        private readonly ConcurrentDictionary<string, Bar> _lastReceivedBar = new();
        private readonly object _lockObject = new object();

        public BarTrailingStopManager(ILogger logger, MonitorConfig config)
        {
            _logger = logger;
            _config = config;
        }

        public double? ProcessNewBar(Bar bar, PositionInfo position)
        {
            if (!_config.UseBarBasedTrailing || position.Quantity <= 0)
                return null;

            lock (_lockObject)
            {
                var symbol = position.Contract.Symbol;
                
                if (_config.BarDebug)
                {
                    _logger.Information("BAR PROCESSING START: {Symbol} {Time} O:{Open:F2} H:{High:F2} L:{Low:F2} C:{Close:F2}", 
                        symbol, GetBarTimeString(bar), bar.Open, bar.High, bar.Low, bar.Close);
                }
                
                // Initialize bar history for this symbol if needed
                if (!_barHistory.ContainsKey(symbol))
                {
                    _barHistory[symbol] = new Queue<Bar>();
                }

                // IMMEDIATE PROCESSING: Process the current bar immediately as completed
                // This eliminates the 10-second delay from waiting for the next bar
                double? trailingResult = ProcessCompletedBar(bar, position);
                
                if (_config.BarDebug)
                {
                    _logger.Information("BAR PROCESSED IMMEDIATELY: {Symbol} {Time} - No delay", 
                        symbol, GetBarTimeString(bar));
                }
                
                return trailingResult;
            }
        }

        private double? ProcessCompletedBar(Bar completedBar, PositionInfo position)
        {
            var symbol = position.Contract.Symbol;
            var barQueue = _barHistory[symbol];
            
            // Debug output for completed bars
            if (_config.BarDebug)
            {
                _logger.Information("COMPLETED BAR: {Symbol} {Time} O:{Open:F2} H:{High:F2} L:{Low:F2} C:{Close:F2}", 
                    symbol, GetBarTimeString(completedBar), completedBar.Open, completedBar.High, completedBar.Low, completedBar.Close);
            }
            
            // Add completed bar to history
            barQueue.Enqueue(completedBar);
            
            // Keep only the required number of bars for lookback
            while (barQueue.Count > _config.BarTrailingLookback)
            {
                barQueue.Dequeue();
            }

            // Check if this completed bar qualifies for trailing stop update
            if (!ShouldUpdateTrailingStop(completedBar, position))
            {
                if (_config.BarDebug)
                {
                    _logger.Information("Completed bar does not qualify for trailing stop update: {Symbol} Close:{Close:F2} Open:{Open:F2} EntryPrice:{EntryPrice:F2}", 
                        symbol, completedBar.Close, completedBar.Open, position.AveragePrice);
                }
                return null;
            }

            // Calculate new trailing stop based on lookback period
            // Use only the last BarTrailingLookback bars for calculation
            var allBars = barQueue.ToArray();
            var barsForCalculation = allBars.Skip(Math.Max(0, allBars.Length - _config.BarTrailingLookback)).ToArray();
            var newStopPrice = CalculateTrailingStop(barsForCalculation);
            
            // Only update if new stop is higher than current stop (for long positions)
            if (position.StopLossPrice.HasValue && newStopPrice <= position.StopLossPrice.Value)
            {
                if (_config.BarDebug)
                {
                    _logger.Information("New trailing stop {NewStop:F2} is not higher than current stop {CurrentStop:F2} for {Symbol}", 
                        newStopPrice, position.StopLossPrice.Value, symbol);
                }
                return null;
            }

            _logger.Information("Bar-based trailing stop triggered for {Symbol}: Completed bar Close:{Close:F2} > Entry:{Entry:F2}, New Stop:{NewStop:F2} (Lookback: {Lookback} bars)", 
                symbol, completedBar.Close, position.AveragePrice, newStopPrice, _config.BarTrailingLookback);

            return newStopPrice;
        }

        private bool IsBarCompleted(Bar bar)
        {
            // Parse bar timestamp (format: "yyyyMMdd-HH:mm:ss")
            if (!DateTime.TryParseExact(bar.Time, "yyyyMMdd-HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var barTime))
            {
                _logger.Warning("Could not parse bar timestamp: {BarTime}", bar.Time);
                return false;
            }

            // Use timezone-resistant MM:SS comparison for bar completion
            var currentTime = DateTime.Now;
            
            // Calculate bar end time in MM:SS format
            var barEndTime = barTime.AddSeconds(_config.BarInterval);
            
            // Extract MM:SS components (timezone-independent)
            var barEndMinutes = barEndTime.Minute;
            var barEndSeconds = barEndTime.Second;
            var currentMinutes = currentTime.Minute;
            var currentSeconds = currentTime.Second;
            
            // Convert to total seconds within the hour for easy comparison
            var barEndTotalSeconds = barEndMinutes * 60 + barEndSeconds;
            var currentTotalSeconds = currentMinutes * 60 + currentSeconds;
            
            // Handle minute rollover (e.g., bar ends at 00:05, current is 59:55)
            // If bar end seconds < bar start seconds, it crossed the hour boundary
            var barStartMinutes = barTime.Minute;
            var barStartSeconds = barTime.Second;
            var barStartTotalSeconds = barStartMinutes * 60 + barStartSeconds;
            
            bool isCompleted;
            if (barEndTotalSeconds < barStartTotalSeconds)
            {
                // Bar crosses hour boundary (e.g., 59:55 + 10s = 00:05)
                isCompleted = currentTotalSeconds >= barEndTotalSeconds || currentTotalSeconds < barStartTotalSeconds;
            }
            else
            {
                // Normal case: bar within same hour
                isCompleted = currentTotalSeconds >= barEndTotalSeconds;
            }
            
            if (_config.BarDebug && isCompleted)
            {
                _logger.Information("BAR COMPLETION DETECTED: Bar {BarStart} -> {BarEnd}, Current {Current} (MM:SS logic)", 
                    $"{barStartMinutes:D2}:{barStartSeconds:D2}", 
                    $"{barEndMinutes:D2}:{barEndSeconds:D2}", 
                    $"{currentMinutes:D2}:{currentSeconds:D2}");
            }
            else if (_config.BarDebug)
            {
                _logger.Debug("Bar still live: Bar {BarStart} -> {BarEnd}, Current {Current} (waiting for completion)", 
                    $"{barStartMinutes:D2}:{barStartSeconds:D2}", 
                    $"{barEndMinutes:D2}:{barEndSeconds:D2}", 
                    $"{currentMinutes:D2}:{currentSeconds:D2}");
            }

            return isCompleted;
        }

        private string GetBarEndTimeString(Bar bar)
        {
            if (DateTime.TryParseExact(bar.Time, "yyyyMMdd-HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var barTime))
            {
                var barEndTime = barTime.AddSeconds(_config.BarInterval);
                return barEndTime.ToString("HH:mm:ss");
            }
            return "unknown";
        }

        private string GetBarTimeString(Bar bar)
        {
            if (DateTime.TryParseExact(bar.Time, "yyyyMMdd-HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var barTime))
            {
                return barTime.ToString("HH:mm:ss");
            }
            return bar.Time;
        }

        private bool ShouldUpdateTrailingStop(Bar bar, PositionInfo position)
        {
            // Bar must close positive (close > open) - strict greater than
            if (bar.Close <= bar.Open)
            {
                if (_config.BarDebug)
                {
                    _logger.Debug("Bar rejected: Close {Close:F4} <= Open {Open:F4} (not positive)", bar.Close, bar.Open);
                }
                return false;
            }

            // Bar must close above entry price - strict greater than
            if (bar.Close <= position.AveragePrice)
            {
                if (_config.BarDebug)
                {
                    _logger.Debug("Bar rejected: Close {Close:F4} <= Entry {Entry:F4} (not above entry)", bar.Close, position.AveragePrice);
                }
                return false;
            }

            if (_config.BarDebug)
            {
                _logger.Information("Bar qualifies for trailing stop: Close {Close:F4} > Open {Open:F4} AND Close > Entry {Entry:F4}", 
                    bar.Close, bar.Open, position.AveragePrice);
            }

            return true;
        }

        private double CalculateTrailingStop(Bar[] bars)
        {
            if (bars.Length == 0)
                throw new ArgumentException("No bars provided for trailing stop calculation");

            // Find the lowest low among all bars in the lookback period
            var lowestLow = bars.Min(b => b.Low);
            
            // Apply the trailing offset
            var trailingStop = lowestLow - _config.BarTrailingOffset;

            _logger.Debug("Trailing stop calculation: Lowest Low of {BarCount} bars = {LowestLow:F2}, Offset = {Offset:F2}, Stop = {Stop:F2}", 
                bars.Length, lowestLow, _config.BarTrailingOffset, trailingStop);

            return trailingStop;
        }

        public void ClearHistory(string symbol)
        {
            lock (_lockObject)
            {
                if (_barHistory.ContainsKey(symbol))
                {
                    _barHistory[symbol].Clear();
                }
                
                if (_lastReceivedBar.ContainsKey(symbol))
                {
                    _lastReceivedBar.TryRemove(symbol, out _);
                }
                
                _logger.Debug("Cleared bar history and last received bar for {Symbol}", symbol);
            }
        }

        public void ClearAllHistory()
        {
            lock (_lockObject)
            {
                _barHistory.Clear();
                _lastReceivedBar.Clear();
                _logger.Debug("Cleared all bar history and last received bars");
            }
        }

        public int GetBarHistoryCount(string symbol)
        {
            lock (_lockObject)
            {
                return _barHistory.ContainsKey(symbol) ? _barHistory[symbol].Count : 0;
            }
        }

        public Bar[] GetBarHistory(string symbol)
        {
            lock (_lockObject)
            {
                if (_barHistory.ContainsKey(symbol))
                {
                    return _barHistory[symbol].ToArray();
                }
                return Array.Empty<Bar>();
            }
        }

        public string GetTrailingStopStatus(string symbol)
        {
            lock (_lockObject)
            {
                var barCount = GetBarHistoryCount(symbol);
                var bars = GetBarHistory(symbol);
                
                if (barCount == 0)
                    return $"No bar history for {symbol}";

                var latestBar = bars.LastOrDefault();
                if (latestBar == null)
                    return $"No latest bar for {symbol}";

                var lowestLow = bars.Min(b => b.Low);
                var calculatedStop = lowestLow - _config.BarTrailingOffset;

                return $"{symbol}: {barCount} bars, Latest: O:{latestBar.Open:F2} H:{latestBar.High:F2} L:{latestBar.Low:F2} C:{latestBar.Close:F2}, " +
                       $"Lowest Low: {lowestLow:F2}, Calculated Stop: {calculatedStop:F2}";
            }
        }

        public void UpdateConfiguration(MonitorConfig newConfig)
        {
            // If lookback period changed, we might need to adjust bar history
            if (newConfig.BarTrailingLookback != _config.BarTrailingLookback)
            {
                lock (_lockObject)
                {
                    var maxBars = newConfig.BarTrailingLookback + 1;
                    foreach (var queue in _barHistory.Values)
                    {
                        while (queue.Count > maxBars)
                        {
                            queue.Dequeue();
                        }
                    }
                }
                _logger.Information("Updated bar trailing lookback period from {OldLookback} to {NewLookback}", 
                    _config.BarTrailingLookback, newConfig.BarTrailingLookback);
            }

            if (newConfig.BarTrailingOffset != _config.BarTrailingOffset)
            {
                _logger.Information("Updated bar trailing offset from {OldOffset:F2} to {NewOffset:F2}", 
                    _config.BarTrailingOffset, newConfig.BarTrailingOffset);
            }
        }
    }
}
