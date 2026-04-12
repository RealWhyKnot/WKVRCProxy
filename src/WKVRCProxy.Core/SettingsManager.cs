using System;
using System.IO;
using System.Text.Json;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Models;

namespace WKVRCProxy.Core;

public class SettingsManager
{
    private readonly string _filePath;
    private AppConfig _config;
    private readonly object _lock = new object();

    // Injected after construction to avoid circular dependency (Logger depends on SettingsManager).
    // Call SetLogger() once the Logger is created.
    private Logger? _logger;

    public AppConfig Config => _config;

    public SettingsManager(string baseDir)
    {
        _filePath = Path.Combine(baseDir, "app_config.json");
        _config = Load();
    }

    public void SetLogger(Logger logger)
    {
        _logger = logger;
    }

    private AppConfig Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                var cfg = new AppConfig();
#if DEBUG
                cfg.DebugMode = true;
#endif
                return cfg;
            }

            try
            {
                string json = File.ReadAllText(_filePath);
                var config = JsonSerializer.Deserialize(json, CoreJsonContext.Default.AppConfig);
                return config ?? new AppConfig();
            }
            catch (Exception ex)
            {
                // Logger not yet available at construction time — write directly to a fallback file
                try
                {
                    string errFile = Path.Combine(Path.GetDirectoryName(_filePath)!, "settings_load_error.log");
                    File.WriteAllText(errFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Failed to load app_config.json: {ex}\n");
                }
                catch { }
                return new AppConfig();
            }
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                string json = JsonSerializer.Serialize(_config, CoreJsonContext.Default.AppConfig);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                _logger?.Error("Failed to save settings: " + ex.Message, ex);
            }
        }
    }
}
