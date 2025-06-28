# IB Position Monitor

A fully reactive C# program for automatic monitoring of trading positions via the Interactive Brokers API (TWS/IB Gateway). The program automatically sets stop-loss orders and can manage break-even stops - exclusively for long positions.

## Features

- ✅ **Fully Event-based**: No polling, only reacting to IB API events
- ✅ **Automatic Stop-Loss Orders**: Set immediately when position is opened
- ✅ **Break-Even Management**: Automatic movement to break-even on profit
- ✅ **Dynamic Adjustment**: Stop-loss updated when average price changes
- ✅ **JSON Configuration**: All settings controllable via config.json
- ✅ **Named Pipe Interface**: External control via \\.\pipe\ibmonitor
- ✅ **Console Interface**: Interactive commands directly in the program
- ✅ **Automatic Reconnection**: Robust against connection failures
- ✅ **Flexible Logging**: Console and optional file output
- ✅ **Position Script**: Execute external scripts on position opening

## Requirements

- .NET 8.0 or higher
- Interactive Brokers TWS or IB Gateway
- Active IB API connection (typically port 7497 for TWS Paper Trading)

## Installation

1. Clone repository or download source code
2. Compile project:
   ```bash
   dotnet build
   ```

## Configuration

Create a `config.json` file in the execution directory:

```json
{
  "port": 7497,
  "clientid": 1,
  "stoploss": 0.20,
  "marketoffset": "2%",
  "breakeven": 100.0,
  "breakevenoffset": 0.01,
  "symbol": "AAPL",
  "positionopenscript": "C:\\Scripts\\position_opened.bat",
  "loglevel": "INFO",
  "logfile": true
}
```

### Configuration Options

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `port` | int | 7497 | TCP port for IB Gateway/TWS connection |
| `clientid` | int | 1 | Client ID for IB connection |
| `stoploss` | double | 0.20 | Stop-loss distance in USD below average price |
| `marketoffset` | string | "2%" | Limit offset for stop-limit orders (absolute or %) |
| `breakeven` | double? | null | Profit threshold in USD for break-even trigger |
| `breakevenoffset` | double | 0.01 | Break-even stop markup above average price |
| `symbol` | string | null | **Required**: Ticker symbol to monitor |
| `positionopenscript` | string? | null | Path to script for position opening |
| `loglevel` | string | "INFO" | Log level: TRACE, DEBUG, INFO, WARN, ERROR |
| `logfile` | bool | false | Additional output to log file |

## Usage

### Starting the Program

```bash
dotnet run
```

or run the compiled .exe directly.

### Console Commands

After startup, commands can be entered directly in the console:

#### SET Commands

```bash
# Change stop-loss distance
set stoploss 0.25

# Change market offset (absolute or percentage)
set marketoffset 0.05
set marketoffset 3%

# Set break-even trigger
set breakeven trigger 150.0

# Change break-even offset
set breakeven offset 0.02

# Manually trigger break-even
set breakeven force

# Change monitored symbol
set symbol TSLA
```

#### SHOW Commands

```bash
# Display current positions
show positions

# Display current configuration
show config

# Display account status
show account

# Show help
help

# Exit program
exit
```

### Named Pipe Interface

The program creates a Named Pipe server at `\\.\pipe\ibmonitor`. External programs can send commands:

#### PowerShell Example:
```powershell
$pipe = new-object System.IO.Pipes.NamedPipeClientStream(".", "ibmonitor", [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect()
$writer = new-object System.IO.StreamWriter($pipe)
$reader = new-object System.IO.StreamReader($pipe)

$writer.WriteLine("show positions")
$writer.Flush()
$response = $reader.ReadLine()
Write-Host $response

$pipe.Dispose()
```

#### C# Example:
```csharp
using var pipe = new NamedPipeClientStream(".", "ibmonitor", PipeDirection.InOut);
await pipe.ConnectAsync();
using var writer = new StreamWriter(pipe);
using var reader = new StreamReader(pipe);

await writer.WriteLineAsync("set stoploss 0.30");
await writer.FlushAsync();
var response = await reader.ReadLineAsync();
Console.WriteLine(response);
```

## How It Works

### Position Monitoring

1. **Connection**: Automatic connection to IB Gateway/TWS
2. **Position Detection**: Monitoring only the configured symbol
3. **Stop-Loss Creation**: Immediate creation when position opens
4. **Dynamic Updates**: Automatic adjustment when average price changes
5. **Break-Even Trigger**: Optional movement to break-even on profit

### Stop-Loss Logic (Long Positions Only)

```
Stop Price = Average Price - StopLoss
Limit Price = Stop Price - MarketOffset
```

Example:
- Average Price: $100.00
- StopLoss: $0.20
- MarketOffset: 2%
- Stop Price: $99.80
- Limit Price: $97.80 (99.80 - 2%)

### Break-Even Logic

```
Break-Even triggered when: (Market Price - Average Price) × Quantity ≥ BreakEven
New Stop Price = Average Price + BreakEvenOffset
```

## Logging

The program uses structured logging with Serilog:

- **Console**: Always active
- **File**: Optional via `"logfile": true`
- **Format**: `Log_YYYY-MM-DD_HH-MM-SS.log`

Log levels cannot be changed at runtime - only via restart with new configuration.

## Error Handling

- **Connection Failures**: Automatic reconnection every 5 seconds
- **Order Errors**: Detailed error logging
- **Configuration Errors**: Validation at startup
- **Graceful Shutdown**: Clean exit on Ctrl+C

## Limitations

- **Long Positions Only**: No short trading support
- **Single Symbol**: Monitors only one symbol at a time
- **Stop-Limit Orders**: Uses STP LMT order type exclusively
- **USD Prices**: StopLoss and BreakEven values in USD

## Architecture

### Services

- **ConfigService**: Configuration loading and validation
- **IBConnectionService**: IB API connection management
- **PositionMonitorService**: Core position monitoring logic
- **CommandService**: Command processing and responses
- **ConsoleService**: Interactive console interface
- **NamedPipeService**: External communication interface
- **LoggingService**: Structured logging setup

### Event Flow

1. IB API position updates → PositionMonitorService
2. Position changes → Stop-loss order management
3. Profit threshold reached → Break-even trigger
4. Commands received → Configuration updates
5. All activities → Structured logging

## Development

### Building

```bash
dotnet build --configuration Release
```

### Running Tests

```bash
dotnet test
```

### Debugging

Set log level to "DEBUG" in config.json for detailed information:

```json
{
  "loglevel": "DEBUG"
}
```

## License

This software is provided as-is for educational and personal use. Use at your own risk. The authors are not responsible for any trading losses.

## Disclaimer

**Trading Risk Warning**: Automated trading involves significant risk. This software is provided for educational purposes only. Always test thoroughly in a paper trading environment before using with real money. Past performance does not guarantee future results. 