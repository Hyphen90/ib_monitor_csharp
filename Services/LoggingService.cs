using Serilog;
using Serilog.Events;
using IBMonitor.Config;

namespace IBMonitor.Services
{
    public static class LoggingService
    {
        public static ILogger CreateLogger(MonitorConfig config)
        {
            var loggerConfig = new LoggerConfiguration();

            // Log Level
            var logLevel = ParseLogLevel(config.LogLevel);
            loggerConfig.MinimumLevel.Is(logLevel);

            // Console Logging
            loggerConfig.WriteTo.Console(
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

            // File Logging
            if (config.LogFile)
            {
                var logFileName = "Log_.log";
                loggerConfig.WriteTo.File(
                    path: logFileName,
                    outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30);
            }

            return loggerConfig.CreateLogger();
        }

        private static LogEventLevel ParseLogLevel(string logLevel)
        {
            return logLevel.ToUpperInvariant() switch
            {
                "TRACE" => LogEventLevel.Verbose,
                "DEBUG" => LogEventLevel.Debug,
                "INFO" => LogEventLevel.Information,
                "WARN" => LogEventLevel.Warning,
                "ERROR" => LogEventLevel.Error,
                "FATAL" => LogEventLevel.Fatal,
                _ => LogEventLevel.Information
            };
        }
    }
}
