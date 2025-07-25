using IBMonitor.Config;
using IBMonitor.Services;
using Serilog;
using System;
using System.IO;
using System.Reflection;

namespace IBMonitor
{
    class Program
    {
        private static ILogger? _logger;
        private static IBConnectionService? _ibService;
        private static PositionMonitorService? _positionService;
        private static NamedPipeService? _pipeService;
        private static ConsoleService? _consoleService;
        private static readonly TaskCompletionSource<bool> _shutdownTcs = new();
        private static bool _isShuttingDown = false;

        static async Task Main(string[] args)
        {
            try
            {
                // Get build time from assembly
                var assembly = Assembly.GetExecutingAssembly();
                DateTime buildTime;
                
                try
                {
                    var location = assembly.Location;
                    if (string.IsNullOrEmpty(location))
                    {
                        // Fallback for single-file deployments or when Location is empty
                        buildTime = File.GetLastWriteTime(Environment.ProcessPath ?? Assembly.GetExecutingAssembly().CodeBase?.Replace("file:///", ""));
                    }
                    else
                    {
                        buildTime = File.GetLastWriteTime(location);
                    }
                }
                catch
                {
                    // Ultimate fallback - use current time
                    buildTime = DateTime.Now;
                }
                
                Console.WriteLine($"=== IB Position Monitor === (Built: {buildTime:yyyy-MM-dd HH:mm:ss})");
                Console.WriteLine("Loading configuration...");

                // Load configuration
                var configService = new ConfigService(Log.Logger);
                var config = configService.LoadConfig();

                // Setup logging
                _logger = LoggingService.CreateLogger(config);
                Log.Logger = _logger;

                // Validate configuration
                try
                {
                    configService.ValidateConfig(config);
                }
                catch (ArgumentException ex)
                {
                    _logger.Error("Configuration error: {Error}", ex.Message);
                    Console.WriteLine($"Configuration error: {ex.Message}");
                    Console.WriteLine("Press any key to exit...");
                    Console.ReadKey();
                    return;
                }

                _logger.Information("Starting IB Position Monitor...");
                _logger.Information("Configuration: Symbol={Symbol}, Port={Port}, ClientId={ClientId}, StopLoss={StopLoss}", 
                    config.Symbol, config.Port, config.ClientId, config.StopLoss);

                // Initialize services
                _ibService = new IBConnectionService(_logger, config);
                _positionService = new PositionMonitorService(_logger, config, _ibService);
                var barTrailingManager = new BarTrailingStopManager(_logger, config);

                var commandService = new CommandService(_logger, config, _positionService, _ibService, configService, barTrailingManager);
                
                // Setup console and pipe services
                _consoleService = new ConsoleService(_logger, commandService);
                _pipeService = new NamedPipeService(_logger, commandService, _consoleService);

                // Setup position event handlers
                _positionService.PositionOpened += OnPositionOpened;
                _positionService.PositionClosed += OnPositionClosed;
                _positionService.PositionChanged += OnPositionChanged;

                // Setup graceful shutdown
                Console.CancelKeyPress += OnCancelKeyPress;
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

                // Connect to IB
                var connected = await _ibService.ConnectAsync();
                
                // Show startup info
                _consoleService.ShowStartupInfo(config.Symbol, config.Port, connected);

                if (!connected)
                {
                    _logger.Warning("Failed to connect to IB. Check if TWS/Gateway is running and port {Port} is correct.", config.Port);
                }

                // Keep the application running
                await WaitForShutdownAsync();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Critical error starting the program");
                Console.WriteLine($"Critical error: {ex.Message}");
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
        }

        private static void OnPositionOpened(Models.PositionInfo position)
        {
            var message = $"Position opened: {position.Contract.Symbol} - Qty: {position.Quantity}, AvgPrice: {position.AveragePrice:F2}";
            _consoleService?.ShowPositionUpdate(message);
        }

        private static void OnPositionClosed(Models.PositionInfo position)
        {
            var message = $"Position closed: {position.Contract.Symbol}";
            _consoleService?.ShowPositionUpdate(message);
        }

        private static void OnPositionChanged(Models.PositionInfo position)
        {
            var message = $"Position changed: {position.Contract.Symbol} - Qty: {position.Quantity}, AvgPrice: {position.AveragePrice:F2}";
            _consoleService?.ShowPositionUpdate(message);
        }

        private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true; // Prevent immediate termination
            _logger?.Information("Shutting down program on user request...");
            Shutdown();
        }

        private static void OnProcessExit(object? sender, EventArgs e)
        {
            Shutdown();
        }

        private static void Shutdown()
        {
            // Prevent multiple shutdown calls
            if (_isShuttingDown)
                return;
                
            _isShuttingDown = true;
            
            try
            {
                _logger?.Information("Shutting down services...");

                _pipeService?.Dispose();
                _consoleService?.Dispose();
                _ibService?.Disconnect();
                _positionService = null;

                _logger?.Information("IB Position Monitor stopped");
                
                // Signal shutdown completion
                _shutdownTcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error during shutdown");
                _shutdownTcs.TrySetResult(true);
            }
        }

        private static async Task WaitForShutdownAsync()
        {
            // Wait for shutdown signal
            await _shutdownTcs.Task;
        }
    }
}
