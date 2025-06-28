using Serilog;

namespace IBMonitor.Services
{
    public class ConsoleService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly CommandService _commandService;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Task _consoleTask;

        public ConsoleService(ILogger logger, CommandService commandService)
        {
            _logger = logger;
            _commandService = commandService;
            _cancellationTokenSource = new CancellationTokenSource();
            
            _consoleTask = Task.Run(RunConsoleAsync);
        }

        private async Task RunConsoleAsync()
        {
            Console.WriteLine();
            Console.WriteLine("=== IB Position Monitor Started ===");
            Console.WriteLine("Use 'help' for available commands or 'exit' to quit.");
            Console.WriteLine("Commands can also be sent via Named Pipe: \\\\.\\pipe\\ibmonitor");
            Console.WriteLine();

            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    Console.Write("> ");
                    
                    // Use Task.Run to make Console.ReadLine cancellable
                    var readTask = Task.Run(() => Console.ReadLine());
                    var completedTask = await Task.WhenAny(readTask, Task.Delay(-1, _cancellationTokenSource.Token));
                    
                    if (completedTask == readTask)
                    {
                        var input = await readTask;
                        
                        if (string.IsNullOrWhiteSpace(input))
                            continue;

                        if (input.Trim().ToLowerInvariant() == "exit")
                        {
                            Console.WriteLine("Program is shutting down...");
                            Environment.Exit(0);
                            break;
                        }

                        try
                        {
                            var response = await _commandService.ProcessCommandAsync(input);
                            Console.WriteLine(response);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error: {ex.Message}");
                            _logger.Error(ex, "Error processing console command: {Input}", input);
                        }
                    }
                    else
                    {
                        // Cancellation was requested
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error in Console Service");
                    await Task.Delay(1000, _cancellationTokenSource.Token);
                }
            }
        }

        public void ShowStartupInfo(string configSymbol, int port, bool isConnected)
        {
            Console.WriteLine($"Monitored Symbol: {configSymbol ?? "Not configured"}");
            Console.WriteLine($"IB Port: {port}");
            Console.WriteLine($"Connection Status: {(isConnected ? "Connected" : "Disconnected")}");
            Console.WriteLine();
        }

        public void ShowPositionUpdate(string message)
        {
            // Display position updates in a way that doesn't interfere with console input
            var currentLine = Console.CursorTop;
            Console.SetCursorPosition(0, currentLine);
            Console.Write(new string(' ', Console.WindowWidth - 1)); // Clear current line
            Console.SetCursorPosition(0, currentLine);
            Console.WriteLine($"[POSITION] {message}");
            Console.Write("> ");
        }

        public async Task<string> ProcessPipeCommandAsync(string command)
        {
            try
            {
                // Clear current line and show the command as if manually entered
                var currentLine = Console.CursorTop;
                Console.SetCursorPosition(0, currentLine);
                Console.Write(new string(' ', Math.Min(Console.WindowWidth - 1, 80))); // Clear current line
                Console.SetCursorPosition(0, currentLine);
                Console.WriteLine($"[PIPE] > {command}");

                // Process the command
                var response = await _commandService.ProcessCommandAsync(command);
                
                // Display the response
                Console.WriteLine(response);
                
                // Show new prompt
                Console.Write("> ");
                
                return response;
            }
            catch (Exception ex)
            {
                var errorMsg = $"Error: {ex.Message}";
                Console.WriteLine(errorMsg);
                Console.Write("> ");
                _logger.Error(ex, "Error processing pipe command: {Command}", command);
                return errorMsg;
            }
        }

        public void Dispose()
        {
            try
            {
                if (!_cancellationTokenSource.IsCancellationRequested)
                {
                    _cancellationTokenSource.Cancel();
                }
                _consoleTask?.Wait(TimeSpan.FromSeconds(2));
                _cancellationTokenSource.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed, ignore
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error stopping Console Service");
            }
        }
    }
} 