using Serilog;
using IBMonitor.Config;
using IBMonitor.Models;
using System.Text.RegularExpressions;
using IBApi;

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
                // Check if close mode is active and block most commands
                if (_positionService.IsClosing)
                {
                    // Only allow help and show commands during close mode
                    if (command == "help" || command == "show")
                    {
                        // Allow these commands to proceed
                    }
                    else if (command == "c")
                    {
                        return "Close command is already in progress. Please wait for all positions to be closed.";
                    }
                    else if (command.StartsWith("b") && command.Length > 1)
                    {
                        return "Buy commands are blocked during close mode. Please wait for all positions to be closed.";
                    }
                    else if (command == "set")
                    {
                        return "Set commands are blocked during close mode. Please wait for all positions to be closed.";
                    }
                    else if (command == "exit")
                    {
                        return "Exit command is blocked during close mode. Please wait for all positions to be closed.";
                    }
                    else
                    {
                        return "Commands are blocked during close mode. Please wait for all positions to be closed. Use 'help' or 'show' for information.";
                    }
                }

                // Check for buy commands (B100, B100,4.36, etc.)
                if (command.StartsWith("b") && command.Length > 1)
                {
                    return await HandleBuyCommand(input.Trim());
                }

                return command switch
                {
                    "c" => await HandleCloseCommand(),
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
                "buyoffset" => SetBuyOffset(value),
                "selloffset" => SetSellOffset(value),
                "maxshares" => SetMaxShares(value),
                "symbol" => SetSymbol(value),
                "breakeven" => HandleBreakEvenCommand(parts),
                _ => $"Unknown 'set' command: {subCommand}"
            };
        }

        private string HandleBreakEvenCommand(string[] parts)
        {
            if (parts.Length < 3)
                return "Invalid 'set breakeven' syntax. Examples: 'set breakeven trigger 100' or 'set breakeven offset 0.02' or 'set breakeven force'";

            var subCommand = parts[2].ToLowerInvariant();
            
            return subCommand switch
            {
                "trigger" => parts.Length >= 4 ? SetBreakEvenTrigger(parts[3]) : "Missing value for 'set breakeven trigger'. Example: 'set breakeven trigger 100'",
                "offset" => parts.Length >= 4 ? SetBreakEvenOffset(parts[3]) : "Missing value for 'set breakeven offset'. Example: 'set breakeven offset 0.02'",
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

        private string SetBuyOffset(string value)
        {
            var oldValue = _config.BuyOffset;
            _config.BuyOffset = value;

            try
            {
                // Test parsing
                var testPrice = 100.0;
                var testOffset = _config.GetBuyOffsetValue(testPrice);
                
                _logger.Information("BuyOffset changed from '{OldValue}' to '{NewValue}'", oldValue, value);
                return $"BuyOffset set to '{value}' (was: '{oldValue}')";
            }
            catch
            {
                _config.BuyOffset = oldValue; // Rollback
                return $"Invalid BuyOffset value: {value}. Use absolute values (e.g. 0.05) or percentage (e.g. 2%)";
            }
        }

        private string SetSellOffset(string value)
        {
            var oldValue = _config.SellOffset;
            _config.SellOffset = value;

            try
            {
                // Test parsing
                var testPrice = 100.0;
                var testOffset = _config.GetSellOffsetValue(testPrice);
                
                _logger.Information("SellOffset changed from '{OldValue}' to '{NewValue}'", oldValue, value);
                return $"SellOffset set to '{value}' (was: '{oldValue}')";
            }
            catch
            {
                _config.SellOffset = oldValue; // Rollback
                return $"Invalid SellOffset value: {value}. Use absolute values (e.g. 0.05) or percentage (e.g. 2%)";
            }
        }

        private string SetMaxShares(string value)
        {
            var oldValue = _config.MaxShares;
            
            if (value.ToLowerInvariant() == "unlimited" || value.ToLowerInvariant() == "none")
            {
                _config.MaxShares = null;
                _logger.Information("MaxShares changed from {OldValue} to unlimited", oldValue);
                return $"MaxShares set to unlimited (was: {oldValue?.ToString() ?? "unlimited"})";
            }
            
            if (!int.TryParse(value, out var maxShares) || maxShares <= 0)
                return "Invalid MaxShares value. Must be a positive integer or 'unlimited'.";

            _config.MaxShares = maxShares;
            
            _logger.Information("MaxShares changed from {OldValue} to {NewValue}", oldValue, maxShares);
            return $"MaxShares set to {maxShares} (was: {oldValue?.ToString() ?? "unlimited"})";
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

        private async Task<string> HandleCloseCommand()
        {
            try
            {
                var result = await _positionService.CloseAllPositionsAndSetSellOrder();
                _logger.Information("Close command executed: {Result}", result);
                return result;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error executing close command");
                return $"Error executing close command: {ex.Message}";
            }
        }

        private async Task<string> HandleBuyCommand(string input)
        {
            if (string.IsNullOrEmpty(_config.Symbol))
                return "No symbol configured. Use 'set symbol <SYMBOL>' first.";

            try
            {
                // Parse buy command: B100 or B100,4.36
                var buyPattern = @"^[bB](\d+)(?:,(\d+\.?\d*))?$";
                var match = Regex.Match(input, buyPattern);
                
                if (!match.Success)
                    return "Invalid buy command format. Use: B<quantity> or B<quantity>,<price> (e.g., B100 or B100,4.36)";

                var quantityStr = match.Groups[1].Value;
                var priceStr = match.Groups[2].Value;

                if (!decimal.TryParse(quantityStr, out var quantity) || quantity <= 0)
                    return "Invalid quantity. Must be a positive number.";

                double? limitPrice = null;
                if (!string.IsNullOrEmpty(priceStr))
                {
                    if (!double.TryParse(priceStr, out var price) || price <= 0)
                        return "Invalid price. Must be a positive number.";
                    limitPrice = price;
                }

                return await ProcessBuyOrder(quantity, limitPrice);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error processing buy command: {Input}", input);
                return $"Error processing buy command: {ex.Message}";
            }
        }

        private async Task<string> ProcessBuyOrder(decimal quantity, double? limitPrice = null)
        {
            try
            {
                // Check MaxShares limit before placing order
                if (_config.MaxShares.HasValue)
                {
                    var currentPosition = _positionService.GetPosition(_config.Symbol!);
                    var currentQuantity = currentPosition?.Quantity ?? 0m;
                    var newTotalQuantity = currentQuantity + quantity;

                    if (newTotalQuantity > _config.MaxShares.Value)
                    {
                        var availableShares = Math.Max(0, _config.MaxShares.Value - (int)currentQuantity);
                        return $"Buy order rejected: Would exceed maximum position size of {_config.MaxShares} shares. " +
                               $"Current position: {currentQuantity}, Requested: {quantity}, Available: {availableShares}";
                    }
                }

                var contract = CreateContract(_config.Symbol!);
                var orderId = _ibService.GetNextOrderId();

                Order buyOrder;
                string orderDescription;

                if (limitPrice.HasValue)
                {
                    // Limit order with specified price (round according to IB rules)
                    var roundedLimitPrice = RoundPriceForIB(limitPrice.Value);
                    buyOrder = CreateBuyLimitOrder(quantity, roundedLimitPrice);
                    orderDescription = $"Buy Limit: {quantity} shares at ${FormatPrice(roundedLimitPrice)}";
                }
                else
                {
                    // Market order with ask + offset
                    var askPrice = _positionService.GetCurrentAskPrice(_config.Symbol!);
                    if (askPrice <= 0)
                    {
                        return "Unable to get current ask price. Market data may not be available.";
                    }

                    var buyOffset = _config.GetBuyOffsetValue(askPrice);
                    var calculatedLimitPrice = RoundPriceForIB(askPrice + buyOffset);
                    
                    buyOrder = CreateBuyLimitOrder(quantity, calculatedLimitPrice);
                    orderDescription = $"Buy Limit: {quantity} shares at ${FormatPrice(calculatedLimitPrice)} (Ask: ${FormatPrice(askPrice)} + BuyOffset: ${FormatPrice(buyOffset)})";
                }

                _ibService.PlaceOrder(orderId, contract, buyOrder);

                _logger.Information("Buy order placed: {Symbol} OrderId:{OrderId} {Description}", 
                    _config.Symbol, orderId, orderDescription);

                return $"Buy order placed: {orderDescription} (OrderId: {orderId})";
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error placing buy order");
                return $"Error placing buy order: {ex.Message}";
            }
        }

        private Contract CreateContract(string symbol)
        {
            return new Contract
            {
                Symbol = symbol,
                SecType = "STK",
                Currency = "USD",
                Exchange = "SMART",
                PrimaryExch = "" // Let IB choose the best exchange
            };
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

        private Order CreateBuyLimitOrder(decimal quantity, double limitPrice)
        {
            return new Order
            {
                Action = "BUY",
                OrderType = "LMT",
                TotalQuantity = quantity,
                LmtPrice = limitPrice,
                Tif = "GTC",
                Transmit = true,
                OutsideRth = true  // Allow execution outside regular trading hours
            };
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

BUY Commands:
  B<quantity>                            - Buy shares at Ask + BuyOffset (e.g. B100)
  B<quantity>,<price>                    - Buy shares at specific limit price (e.g. B100,4.36)

CLOSE Commands:
  C                                      - Cancel all orders and place sell limit at Bid - SellOffset

SET Commands:
  set stoploss <value>                   - Set stop-loss distance in USD
  set buyoffset <value or percent>       - Set buy offset (e.g. 0.05 or 2%)
  set selloffset <value or percent>      - Set sell offset (e.g. 0.05 or 2%)
  set maxshares <value or unlimited>     - Set maximum position size (e.g. 500 or unlimited)
  set breakeven trigger <value>          - Set break-even trigger in USD
  set breakeven offset <value>           - Set break-even offset in USD
  set breakeven force                    - Manually trigger break-even
  set symbol <SYMBOL>                    - Set symbol to monitor

SHOW Commands:
  show config                            - Display current configuration

GENERAL:
  help                                   - Show this help
  exit                                   - Exit program

Note: Buy orders automatically create stop-loss orders and adjust them based on average cost.
      The C command is ideal for pre-market use as it uses limit orders instead of market orders.
      MaxShares prevents exceeding the specified position size across multiple buy orders.
      BuyOffset is used for entry orders, SellOffset is used for stop-loss and close orders.";
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
