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
                
                // Initialize bar history for this symbol if needed
                if (!_barHistory.ContainsKey(symbol))
                {
                    _barHistory[symbol] = new Queue<Bar>();
                }

                var barQueue = _barHistory[symbol];
                
                // Add new bar to history
                barQueue.Enqueue(bar);
                
                // Keep only the required number of bars (lookback + current bar)
                var maxBars = _config.BarTrailingLookback + 1;
                while (barQueue.Count > maxBars)
                {
                    barQueue.Dequeue();
                }

                // Check if this bar qualifies for trailing stop update
                if (!ShouldUpdateTrailingStop(bar, position))
                {
                    _logger.Debug("Bar does not qualify for trailing stop update: {Symbol} Close:{Close:F2} Open:{Open:F2} EntryPrice:{EntryPrice:F2}", 
                        symbol, bar.Close, bar.Open, position.AveragePrice);
                    return null;
                }

                // Calculate new trailing stop based on lookback period
                var newStopPrice = CalculateTrailingStop(barQueue.ToArray());
                
                // Only update if new stop is higher than current stop (for long positions)
                if (position.StopLossPrice.HasValue && newStopPrice <= position.StopLossPrice.Value)
                {
                    _logger.Debug("New trailing stop {NewStop:F2} is not higher than current stop {CurrentStop:F2} for {Symbol}", 
                        newStopPrice, position.StopLossPrice.Value, symbol);
                    return null;
                }

                _logger.Information("Bar-based trailing stop triggered for {Symbol}: Bar Close:{Close:F2} > Entry:{Entry:F2}, New Stop:{NewStop:F2} (Lookback: {Lookback} bars)", 
                    symbol, bar.Close, position.AveragePrice, newStopPrice, _config.BarTrailingLookback);

                return newStopPrice;
            }
        }

        private bool ShouldUpdateTrailingStop(Bar bar, PositionInfo position)
        {
            // Bar must close positive (close > open)
            if (bar.Close <= bar.Open)
                return false;

            // Bar must close above entry price
            if (bar.Close <= position.AveragePrice)
                return false;

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
                    _logger.Debug("Cleared bar history for {Symbol}", symbol);
                }
            }
        }

        public void ClearAllHistory()
        {
            lock (_lockObject)
            {
                _barHistory.Clear();
                _logger.Debug("Cleared all bar history");
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
