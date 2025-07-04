# ⚠️ DISCLAIMER & RISK WARNING ⚠️

## 🚨 WICHTIGER HAFTUNGSAUSSCHLUSS / IMPORTANT LIABILITY DISCLAIMER 🚨

**VERWENDUNG AUF EIGENE GEFAHR - USE AT YOUR OWN RISK**

### 🇩🇪 DEUTSCH
**DIESE SOFTWARE WIRD OHNE JEGLICHE GEWÄHRLEISTUNG BEREITGESTELLT. DER AUTOR/ENTWICKLER (ANSÄSSIG IN DEUTSCHLAND) ÜBERNIMMT KEINERLEI HAFTUNG FÜR VERLUSTE, SCHÄDEN ODER ANDERE FOLGEN, DIE DURCH DIE NUTZUNG DIESER SOFTWARE ENTSTEHEN.**

### 🇺🇸 ENGLISH
**THIS SOFTWARE IS PROVIDED WITHOUT ANY WARRANTY. THE AUTHOR/DEVELOPER (BASED IN GERMANY) ASSUMES NO LIABILITY FOR LOSSES, DAMAGES, OR OTHER CONSEQUENCES ARISING FROM THE USE OF THIS SOFTWARE.**

---

## ⚠️ TRADING RISKS / HANDELSRISIKEN

### Financial Risk / Finanzielle Risiken
- **TOTAL LOSS POSSIBLE**: Automated trading can result in substantial financial losses, including total loss of invested capital
- **TOTALVERLUST MÖGLICH**: Automatisiertes Trading kann zu erheblichen finanziellen Verlusten bis hin zum Totalverlust führen
- **Market Volatility**: Financial markets are highly volatile and unpredictable
- **Leverage Risk**: Trading with leverage amplifies both profits and losses

### Technical Risk / Technische Risiken
- **EXPERIMENTAL SOFTWARE**: This is beta/experimental software that may contain bugs, errors, or unexpected behavior
- **API Failures**: Interactive Brokers API connections may fail, causing missed orders or unintended positions
- **Network Issues**: Internet connectivity problems can disrupt trading operations
- **System Failures**: Computer crashes, power outages, or software malfunctions may occur at critical moments

---

## 🚫 NO FINANCIAL ADVICE / KEINE ANLAGEBERATUNG

### Investment Advisory Disclaimer
- **NOT INVESTMENT ADVICE**: This software does not provide investment, financial, or trading advice
- **KEINE ANLAGEBERATUNG**: Diese Software stellt keine Anlage-, Finanz- oder Handelsberatung dar
- **No Recommendations**: No buy/sell recommendations are provided
- **Personal Responsibility**: All trading decisions are solely your responsibility

### Professional Consultation
- **Seek Professional Advice**: Consult qualified financial advisors before making investment decisions
- **Due Diligence**: Conduct your own research and analysis
- **Risk Assessment**: Evaluate your risk tolerance and financial situation

---

## 🌍 INTERNATIONAL COMPLIANCE / INTERNATIONALE COMPLIANCE

### Regulatory Responsibility / Regulatorische Verantwortung
- **Local Laws**: Users are responsible for compliance with local financial regulations
- **EU Regulations**: MiFID II, ESMA guidelines may apply to EU users
- **US Regulations**: SEC, FINRA, CFTC rules may apply to US users
- **Other Jurisdictions**: Comply with your local financial authority requirements

### Licensing Requirements / Lizenzanforderungen
- **Professional Trading**: Professional traders may require licenses or registrations
- **Retail vs. Professional**: Understand your classification under local regulations
- **Cross-Border Trading**: International trading may have additional compliance requirements

---

## ⚖️ LIABILITY LIMITATION / HAFTUNGSBESCHRÄNKUNG

### Complete Disclaimer / Vollständiger Haftungsausschluss
**THE AUTHOR, DEVELOPER, AND ANY CONTRIBUTORS DISCLAIM ALL LIABILITY FOR:**
- Trading losses or missed profits
- Software bugs, errors, or malfunctions
- Data loss or corruption
- System downtime or unavailability
- Incorrect calculations or order executions
- Any direct, indirect, incidental, or consequential damages

**DER AUTOR, ENTWICKLER UND ALLE MITWIRKENDEN SCHLIESSEN JEDE HAFTUNG AUS FÜR:**
- Handelsverluste oder entgangene Gewinne
- Software-Bugs, Fehler oder Störungen
- Datenverlust oder -beschädigung
- Systemausfälle oder Nichtverfügbarkeit
- Falsche Berechnungen oder Orderausführungen
- Alle direkten, indirekten, zufälligen oder Folgeschäden

---

## 🧪 SOFTWARE STATUS / SOFTWARE-STATUS

### Development Stage / Entwicklungsstadium
- **EXPERIMENTAL**: This software is in experimental/beta stage
- **No Warranty**: No warranty of merchantability or fitness for purpose
- **Use in Production**: Not recommended for production trading without extensive testing
- **Paper Trading**: Strongly recommended to test in paper trading environment first

### User Responsibility / Benutzerverantwortung
- **Testing Required**: Thoroughly test all functionality before live trading
- **Monitoring**: Continuously monitor software behavior during operation
- **Backup Plans**: Have manual trading procedures as backup
- **Risk Management**: Implement proper risk management strategies

---

## ✅ ACKNOWLEDGMENT / BESTÄTIGUNG

**BY USING THIS SOFTWARE, YOU ACKNOWLEDGE THAT YOU HAVE READ, UNDERSTOOD, AND AGREE TO ALL TERMS OF THIS DISCLAIMER.**

**DURCH DIE NUTZUNG DIESER SOFTWARE BESTÄTIGEN SIE, DASS SIE ALLE BEDINGUNGEN DIESES HAFTUNGSAUSSCHLUSSES GELESEN, VERSTANDEN UND AKZEPTIERT HABEN.**

---

# IB Position Monitor

A fully reactive C# program for automatic monitoring of trading positions via the Interactive Brokers API (TWS/IB Gateway). The program automatically sets stop-loss orders and can manage break-even stops - exclusively for long positions.

## Features

- ✅ **Fully Event-based**: No polling, only reacting to IB API events
- ✅ **Automatic Stop-Loss Orders**: Set immediately when position is opened
- ✅ **Break-Even Management**: Automatic movement to break-even on profit
- ✅ **Bar-Based Trailing Stops**: Advanced trailing stop using real-time bar data
- ✅ **Dynamic Adjustment**: Stop-loss updated when average price changes
- ✅ **Position Size Limits**: Maximum shares protection with configurable limits
- ✅ **Take-Profit Triggers**: Set conditional close orders at target prices
- ✅ **Buy/Sell Order Management**: Separate offsets for entry and exit orders
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
  "symbol": "AAPL",
  "port": 7497,
  "clientid": 1,
  "stoploss": 0.20,
  "buyoffset": "0.10",
  "selloffset": "2%",
  "usebreakeven": true,
  "breakeven": 100.0,
  "breakevenoffset": 0.01,
  "positionopenscript": "positiontrigger.ahk",
  "loglevel": "INFO",
  "logfile": true,
  "maxshares": 500,
  "usebarbasedtrailing": true,
  "bartrailingoffset": 0.05,
  "bartrailinglookback": 2,
  "barinterval": 10,
  "bardebug": false
}
```

### Configuration Options

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `symbol` | string | null | **Required**: Ticker symbol to monitor |
| `port` | int | 7497 | TCP port for IB Gateway/TWS connection |
| `clientid` | int | 1 | Client ID for IB connection |
| `stoploss` | double | 0.20 | Stop-loss distance in USD below average price |
| `buyoffset` | string | "0.10" | Buy order offset above ask price (absolute or %) |
| `selloffset` | string | "0.10" | Sell order offset below bid price (absolute or %) |
| `usebreakeven` | bool | false | Enable/disable break-even functionality |
| `breakeven` | double? | null | Profit threshold in USD for break-even trigger |
| `breakevenoffset` | double | 0.01 | Break-even stop markup above average price |
| `positionopenscript` | string? | null | Path to script executed when position opens |
| `loglevel` | string | "INFO" | Log level: TRACE, DEBUG, INFO, WARN, ERROR |
| `logfile` | bool | false | Additional output to log file |
| `maxshares` | int? | null | Maximum position size (null = unlimited) |
| `usebarbasedtrailing` | bool | false | Enable bar-based trailing stop functionality |
| `bartrailingoffset` | double | 0.05 | Trailing stop offset in USD from bar highs |
| `bartrailinglookback` | int | 0 | Number of bars to look back for trailing calculation |
| `barinterval` | int | 10 | Bar interval in seconds for real-time data |
| `bardebug` | bool | false | Enable detailed bar processing debug output |

## Usage

### Starting the Program

```bash
dotnet run
```

or run the compiled .exe directly.

### Console Commands

After startup, commands can be entered directly in the console:

#### BUY Commands

```bash
# Buy shares at Ask + BuyOffset
B100                                   # Buy 100 shares at market + offset

# Buy shares at specific limit price
B100,4.36                             # Buy 100 shares at $4.36 limit price
B250,12.50                            # Buy 250 shares at $12.50 limit price
```

#### CLOSE Commands

```bash
# Immediate close - cancel all orders and sell at Bid - SellOffset
C                                     # Close all positions immediately

# Conditional close - set take-profit trigger
C5.43                                 # Set take-profit trigger at $5.43
C12.75                                # Set take-profit trigger at $12.75
```

#### SET Commands

```bash
# Stop-loss and offsets
set stoploss 0.25                     # Set stop-loss distance in USD
set buyoffset 0.10                    # Set buy offset (absolute)
set buyoffset 2%                      # Set buy offset (percentage)
set selloffset 0.05                   # Set sell offset (absolute)
set selloffset 3%                     # Set sell offset (percentage)

# Position limits
set maxshares 500                     # Set maximum position size
set maxshares unlimited               # Remove position size limit

# Break-even management
set breakeven enable                  # Enable break-even functionality
set breakeven disable                 # Disable break-even functionality
set breakeven trigger 100.0           # Set break-even trigger in USD
set breakeven offset 0.02             # Set break-even offset in USD
set breakeven force                   # Manually trigger break-even

# Symbol configuration
set symbol AAPL                      # Change monitored symbol
set symbol TSLA                      # Switch to different symbol
```

#### SHOW Commands

```bash
# Display information
show config                           # Display current configuration
show takeprofit                       # Display take-profit trigger status

# General commands
help                                  # Show command help
exit                                  # Exit program
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

### Bar-Based Trailing Stop Logic

When `usebarbasedtrailing` is enabled, the system uses real-time bar data to create more sophisticated trailing stops:

```
Trailing Stop Price = Highest Bar High (within lookback period) - BarTrailingOffset
```

**Configuration Parameters:**
- `bartrailingoffset`: Distance in USD below the highest bar high
- `bartrailinglookback`: Number of completed bars to analyze (0 = current bar only)
- `barinterval`: Bar duration in seconds (e.g., 10 = 10-second bars)
- `bardebug`: Enable detailed logging of bar processing

**Example:**
- Bar Interval: 10 seconds
- Lookback: 2 bars
- Trailing Offset: $0.05
- Recent bar highs: $100.50, $100.75, $100.90 (current)
- Trailing Stop: $100.85 ($100.90 - $0.05)

**Advantages over traditional trailing:**
- Uses actual price action structure (bar highs)
- Reduces noise from tick-by-tick fluctuations
- More stable trailing behavior
- Configurable lookback for trend analysis

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
