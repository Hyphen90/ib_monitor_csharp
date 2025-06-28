using Serilog;
using IBMonitor.Config;
using IBMonitor.Models;
using System.Text.RegularExpressions;

namespace IBMonitor.Services
{
    public class CommandService
    {
        private readonly ILogger _logger;
        private readonly MonitorConfig _config;
        private readonly PositionMonitorService _positionService;
        private readonly IBConnectionService _ibService;
        private readonly ConfigService _configService;

        public CommandService(ILogger logger, MonitorConfig config, PositionMonitorService positionService, 
            IBConnectionService ibService, ConfigService configService)
        {
            _logger = logger;
            _config = config;
            _positionService = positionService;
            _ibService = ibService;
            _configService = configService;
        }

        public async Task<string> ProcessCommandAsync(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "Invalid command. Use 'help' for assistance.";

            var parts = ParseCommand(input.Trim());
            if (!parts.Any())
                return "Invalid command. Use 'help' for assistance.";

            var command = parts[0].ToLowerInvariant();

            try
            {
                return command switch
                {
                    "set" => HandleSetCommand(parts),
                    "show" => HandleShowCommand(parts),
                    "help" => ShowHelp(),
                    "exit" => HandleExit(),
                    _ => "Unknown command. Use 'help' for assistance."
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error processing command: {Command}", input);
                return $"Error processing command: {ex.Message}";
            }
        }

        private string HandleSetCommand(string[] parts)
        {
            if (parts.Length < 3)
                return "Invalid 'set' syntax. Example: set stoploss 0.25";

            var subCommand = parts[1].ToLowerInvariant();
            var value = parts[2];

            return subCommand switch
            {
                "stoploss" => SetStopLoss(value),
                "marketoffset" => SetMarketOffset(value),
                "symbol" => SetSymbol(value),
                "breakeven" => HandleBreakEvenCommand(parts),
                _ => $"Unknown 'set' command: {subCommand}"
            };
        }

        private string HandleBreakEvenCommand(string[] parts)
        {
            if (parts.Length < 4)
                return "Invalid 'set breakeven' syntax. Examples: 'set breakeven trigger 100' or 'set breakeven offset 0.02' or 'set breakeven force'";

            var subCommand = parts[2].ToLowerInvariant();
            
            return subCommand switch
            {
                "trigger" => SetBreakEvenTrigger(parts[3]),
                "offset" => SetBreakEvenOffset(parts[3]),
                "force" => ForceBreakEven(),
                _ => $"Unknown 'breakeven' command: {subCommand}"
            };
        }

        private string SetStopLoss(string value)
        {
            if (!double.TryParse(value, out var stopLoss) || stopLoss <= 0)
                return "Invalid StopLoss value. Must be a positive number.";

            var oldValue = _config.StopLoss;
            _config.StopLoss = stopLoss;
            
            // Update existing positions
            if (!string.IsNullOrEmpty(_config.Symbol))
            {
                _positionService.UpdateStopLoss(_config.Symbol, stopLoss);
            }

            _logger.Information("StopLoss changed from {OldValue} to {NewValue}", oldValue, stopLoss);
            return $"StopLoss set to {stopLoss:F2} USD (was: {oldValue:F2} USD)";
        }

        private string SetMarketOffset(string value)
        {
            var oldValue = _config.MarketOffset;
            _config.MarketOffset = value;

            try
            {
                // Test parsing
                var testPrice = 100.0;
                var testOffset = _config.GetMarketOffsetValue(testPrice);
                
                _logger.Information("MarketOffset changed from '{OldValue}' to '{NewValue}'", oldValue, value);
                return $"MarketOffset set to '{value}' (was: '{oldValue}')";
            }
            catch
            {
                _config.MarketOffset = oldValue; // Rollback
                return $"Invalid MarketOffset value: {value}. Use absolute values (e.g. 0.05) or percentage (e.g. 2%)";
            }
        }

        private string SetSymbol(string symbol)
        {
            var oldSymbol = _config.Symbol;
            var newSymbol = symbol.ToUpperInvariant();
            
            // Update symbol and refresh market data subscription
            _positionService.UpdateSymbol(newSymbol);
            
            _logger.Information("Symbol changed from '{OldSymbol}' to '{NewSymbol}'", oldSymbol, newSymbol);
            return $"Symbol set to '{newSymbol}' (was: '{oldSymbol}')";
        }

        private string SetBreakEvenTrigger(string value)
        {
            if (!double.TryParse(value, out var trigger) || trigger <= 0)
                return "Invalid BreakEven Trigger value. Must be a positive number.";

            var oldValue = _config.BreakEven;
            _config.BreakEven = trigger;
            
            _logger.Information("BreakEven Trigger changed from {OldValue} to {NewValue}", oldValue, trigger);
            return $"BreakEven Trigger set to {trigger:F2} USD (was: {oldValue?.ToString("F2") ?? "not set"})";
        }

        private string SetBreakEvenOffset(string value)
        {
            if (!double.TryParse(value, out var offset) || offset <= 0)
                return "Invalid BreakEven Offset value. Must be a positive number.";

            var oldValue = _config.BreakEvenOffset;
            _config.BreakEvenOffset = offset;
            
            _logger.Information("BreakEven Offset changed from {OldValue} to {NewValue}", oldValue, offset);
            return $"BreakEven Offset set to {offset:F2} USD (was: {oldValue:F2} USD)";
        }

        private string ForceBreakEven()
        {
            if (string.IsNullOrEmpty(_config.Symbol))
                return "No symbol configured. Use 'set symbol <SYMBOL>' first.";

            var position = _positionService.GetPosition(_config.Symbol);
            if (position == null || position.IsFlat)
                return $"No open position found for {_config.Symbol}.";

            if (position.BreakEvenTriggered)
                return $"Break-Even already triggered for {_config.Symbol}.";

            _positionService.ForceBreakEven(_config.Symbol);
            return $"Break-Even manually triggered for {_config.Symbol}.";
        }

        private string HandleShowCommand(string[] parts)
        {
            if (parts.Length < 2)
                return "Invalid 'show' syntax. Examples: show config";

            var subCommand = string.Join(" ", parts.Skip(1)).ToLowerInvariant();

            return subCommand switch
            {
                "config" => ShowConfig(),
                _ => $"Unknown 'show' command: {subCommand}"
            };
        }









        private string ShowConfig()
        {
            return $"Current Configuration:\n{_config}";
        }

        private string ShowHelp()
        {
            return @"Available Commands:

SET Commands:
  set stoploss <value>                   - Set stop-loss distance in USD
  set marketoffset <value or percent>    - Set market offset (e.g. 0.05 or 2%)
  set breakeven trigger <value>          - Set break-even trigger in USD
  set breakeven offset <value>           - Set break-even offset in USD
  set breakeven force                    - Manually trigger break-even
  set symbol <SYMBOL>                    - Set symbol to monitor

SHOW Commands:
  show config                            - Display current configuration

GENERAL:
  help                                   - Show this help
  exit                                   - Exit program";
        }

        private string HandleExit()
        {
            Environment.Exit(0);
            return "Program is shutting down...";
        }

        private string[] ParseCommand(string input)
        {
            // Simple parsing that handles quoted arguments
            var regex = new Regex(@"[\""].+?[\""]|[^ ]+");
            var matches = regex.Matches(input);
            
            return matches.Cast<Match>()
                          .Select(m => m.Value.Trim('"'))
                          .ToArray();
        }
    }
} 