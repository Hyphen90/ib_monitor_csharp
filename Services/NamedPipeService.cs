using System.IO.Pipes;
using System.Text;
using Serilog;

namespace IBMonitor.Services
{
    public class NamedPipeService : IDisposable
    {
        private const string PipeName = "ibmonitor";
        private readonly ILogger _logger;
        private readonly CommandService _commandService;
        private readonly ConsoleService? _consoleService;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly Task _serverTask;

        public NamedPipeService(ILogger logger, CommandService commandService, ConsoleService? consoleService = null)
        {
            _logger = logger;
            _commandService = commandService;
            _consoleService = consoleService;
            _cancellationTokenSource = new CancellationTokenSource();
            
            _serverTask = Task.Run(RunServerAsync);
            _logger.Information("Named Pipe Server started: \\\\.\\pipe\\{PipeName}", PipeName);
        }

        private async Task RunServerAsync()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var pipeServer = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    _logger.Debug("Waiting for Named Pipe connection...");

                    await pipeServer.WaitForConnectionAsync(_cancellationTokenSource.Token);
                    _logger.Debug("Named Pipe client connected");

                    // Handle client in background and immediately create new server instance
                    _ = Task.Run(async () => 
                    {
                        try
                        {
                            await HandleClientAsync(pipeServer);
                        }
                        finally
                        {
                            pipeServer.Dispose();
                        }
                    }, _cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error in Named Pipe Server");
                    await Task.Delay(1000, _cancellationTokenSource.Token);
                }
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream pipeServer)
        {
            try
            {
                var buffer = new byte[1024];
                var messageBuilder = new StringBuilder();

                while (pipeServer.IsConnected && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = await pipeServer.ReadAsync(buffer, 0, buffer.Length, _cancellationTokenSource.Token);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Pipe was closed during read operation
                        _logger.Debug("Pipe closed during read operation");
                        break;
                    }
                    catch (IOException ex) when (ex.Message.Contains("Pipe is broken") || 
                                                ex.Message.Contains("pipe has been ended") ||
                                                ex.Message.Contains("Cannot access a closed pipe"))
                    {
                        // Client disconnected during read
                        _logger.Debug("Client disconnected during read: {Message}", ex.Message);
                        break;
                    }
                    
                    if (bytesRead == 0)
                        break;

                    var chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    messageBuilder.Append(chunk);

                    // Check if we have a complete message (ending with newline)
                    var message = messageBuilder.ToString();
                    if (message.Contains('\n'))
                    {
                        var lines = message.Split('\n');
                        
                        // Process all complete lines
                        for (int i = 0; i < lines.Length - 1; i++)
                        {
                            var command = lines[i].Trim();
                            if (!string.IsNullOrEmpty(command))
                            {
                                _logger.Debug("Named Pipe command received: {Command}", command);
                                
                                string response;
                                if (_consoleService != null)
                                {
                                    // Show command and response in console
                                    response = await _consoleService.ProcessPipeCommandAsync(command);
                                }
                                else
                                {
                                    // Fallback to direct command processing
                                    response = await _commandService.ProcessCommandAsync(command);
                                }
                                
                                await SendResponseAsync(pipeServer, response);
                                
                                // Check if pipe is still connected after sending response
                                if (!pipeServer.IsConnected)
                                {
                                    _logger.Debug("Client disconnected after command processing");
                                    break;
                                }
                            }
                        }

                        // Keep the last incomplete line for next iteration
                        messageBuilder.Clear();
                        if (lines.Length > 0 && !string.IsNullOrEmpty(lines[^1]))
                        {
                            messageBuilder.Append(lines[^1]);
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Client closed the pipe - this is normal behavior
                _logger.Debug("Named Pipe client disconnected (pipe closed)");
            }
            catch (IOException ex) when (ex.Message.Contains("Pipe is broken") || 
                                        ex.Message.Contains("pipe has been ended") ||
                                        ex.Message.Contains("Cannot access a closed pipe"))
            {
                // Client disconnected unexpectedly - this is normal behavior
                _logger.Debug("Named Pipe client disconnected (IO error): {Message}", ex.Message);
            }
            catch (OperationCanceledException)
            {
                // Cancellation requested - normal shutdown
                _logger.Debug("Named Pipe client handling cancelled");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unexpected error handling Named Pipe client");
            }
            finally
            {
                _logger.Debug("Named Pipe client disconnected");
            }
        }

        private async Task SendResponseAsync(NamedPipeServerStream pipeServer, string response)
        {
            try
            {
                var responseBytes = Encoding.UTF8.GetBytes(response + "\n");
                await pipeServer.WriteAsync(responseBytes, 0, responseBytes.Length);
                await pipeServer.FlushAsync();
                _logger.Debug("Named Pipe response sent: {Response}", response.Length > 100 ? response.Substring(0, 100) + "..." : response);
            }
            catch (ObjectDisposedException)
            {
                // Client closed the pipe before response could be sent - normal behavior
                _logger.Debug("Cannot send response - pipe closed by client");
            }
            catch (IOException ex) when (ex.Message.Contains("Pipe is broken") || 
                                        ex.Message.Contains("pipe has been ended") ||
                                        ex.Message.Contains("Cannot access a closed pipe"))
            {
                // Client disconnected - normal behavior
                _logger.Debug("Cannot send response - client disconnected: {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unexpected error sending Named Pipe response");
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
                _serverTask?.Wait(TimeSpan.FromSeconds(5));
                _cancellationTokenSource.Dispose();
                _logger.Information("Named Pipe Server stopped");
            }
            catch (ObjectDisposedException)
            {
                // Already disposed, ignore
                _logger.Information("Named Pipe Server stopped");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error stopping Named Pipe Server");
            }
        }
    }
} 