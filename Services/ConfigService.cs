using Newtonsoft.Json;
using Serilog;
using IBMonitor.Config;

namespace IBMonitor.Services
{
    public class ConfigService
    {
        private const string DefaultConfigFileName = "config.json";
        private readonly ILogger _logger;

        public ConfigService(ILogger logger)
        {
            _logger = logger;
        }

        public MonitorConfig LoadConfig(string? configPath = null)
        {
            var filePath = configPath ?? DefaultConfigFileName;
            
            if (!File.Exists(filePath))
            {
                _logger.Information("Configuration file {FilePath} not found. Using default values.", filePath);
                var defaultConfig = new MonitorConfig();
                SaveConfig(defaultConfig, filePath);
                return defaultConfig;
            }

            try
            {
                var json = File.ReadAllText(filePath);
                var config = JsonConvert.DeserializeObject<MonitorConfig>(json) ?? new MonitorConfig();
                _logger.Information("Configuration successfully loaded from {FilePath}", filePath);
                return config;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error loading configuration from {FilePath}. Using default values.", filePath);
                return new MonitorConfig();
            }
        }

        public void SaveConfig(MonitorConfig config, string? configPath = null)
        {
            var filePath = configPath ?? DefaultConfigFileName;
            
            try
            {
                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                File.WriteAllText(filePath, json);
                _logger.Debug("Configuration saved to {FilePath}", filePath);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error saving configuration to {FilePath}", filePath);
            }
        }

        public void ValidateConfig(MonitorConfig config)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(config.Symbol))
            {
                errors.Add("Symbol is required");
            }

            if (config.Port <= 0 || config.Port > 65535)
            {
                errors.Add("Port must be between 1 and 65535");
            }

            if (config.StopLoss <= 0)
            {
                errors.Add("StopLoss must be greater than 0");
            }

            if (config.BreakEvenOffset <= 0)
            {
                errors.Add("BreakEvenOffset must be greater than 0");
            }

            if (errors.Any())
            {
                throw new ArgumentException($"Configuration errors: {string.Join(", ", errors)}");
            }
        }
    }
} 