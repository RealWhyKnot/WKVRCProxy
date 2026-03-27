using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Photino.NET;
using WKVRCProxy.Core;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Services;
using WKVRCProxy.Core.IPC;
using System.Runtime.Versioning;
using System.Windows.Forms;
using Microsoft.WindowsAPICodePack.Dialogs;

namespace WKVRCProxy.UI;

[SupportedOSPlatform("windows")]
class Program
{
    private static PhotinoWindow? _window;
    private static SettingsManager? _settings;
    private static Logger? _logger;
    private static ModuleCoordinator? _coordinator;
    private static bool _isWindowReady = false;

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--setup-hosts")
        {
            SetupHostsBypass();
            return;
        }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) => {
                string crashLog = Path.Combine(baseDir, "crash.log");
                File.WriteAllText(crashLog, "FATAL: " + e.ExceptionObject.ToString());
            };

            RunApp(baseDir);
        }
        catch (Exception ex)
        {
            string crashLog = Path.Combine(baseDir, "startup_crash.log");
            File.WriteAllText(crashLog, "STARTUP ERROR: " + ex.ToString());
            MessageBox.Show("Fatal error during startup.\n\nSee startup_crash.log for details.", "WKVRCProxy", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static void RunApp(string baseDir)
    {
        _settings = new SettingsManager(baseDir);
        _logger = new Logger(baseDir, "System", _settings);
        
        _coordinator = new ModuleCoordinator(_logger, _settings);
        
        var logMonitor = new VrcLogMonitor();
        var codecInstaller = new CodecInstaller();
        var patcherService = new PatcherService();
        var ipcServer = new WebSocketIpcServer();
        var tier2Client = new Tier2WebSocketClient(_logger);
        var hostsManager = new HostsManager();
        var relayPortManager = new RelayPortManager();

        _coordinator.Register(logMonitor);
        _coordinator.Register(codecInstaller);
        _coordinator.Register(patcherService);
        _coordinator.Register(ipcServer);
        _coordinator.Register(tier2Client);
        _coordinator.Register(hostsManager);
        _coordinator.Register(relayPortManager);

        hostsManager.OnIpcRequest += (type, data) => {
            if (_isWindowReady) {
                try {
                    _window?.Invoke(() => {
                        _window?.SendWebMessage(JsonSerializer.Serialize(new { type = type, data = data }));
                    });
                } catch { }
            }
            else {
                // Background delay and retry if window not ready
                Task.Run(async () => {
                    while (!_isWindowReady) await Task.Delay(200);
                    _window?.Invoke(() => {
                        _window?.SendWebMessage(JsonSerializer.Serialize(new { type = type, data = data }));
                    });
                });
            }
        };

        var resEngine = new ResolutionEngine(_logger, _settings, logMonitor, tier2Client);
        ipcServer.OnResolveRequested += async (payload) => await resEngine.ResolveAsync(payload);
        logMonitor.OnVrcPathDetected += (path) => patcherService.UpdateToolsDir(path);
        
        resEngine.OnStatusUpdate += (msg, stats) => {
            if (_isWindowReady) {
                try {
                    _window?.Invoke(() => {
                        _window?.SendWebMessage(JsonSerializer.Serialize(new { type = "STATUS", data = new { message = msg, stats = stats } }));
                    });
                } catch { }
            }
        };

        string webViewDataPath = Path.Combine(baseDir, "WebView2_Data");
        if (!Directory.Exists(webViewDataPath)) Directory.CreateDirectory(webViewDataPath);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", webViewDataPath);

        _window = new PhotinoWindow()
            .SetTitle("WKVRCProxy")
            .SetUseOsDefaultSize(false)
            .SetSize(1200, 800)
            .Center()
            .SetResizable(true)
            .RegisterWebMessageReceivedHandler((s, m) => HandleWebMessage(m))
            .SetLogVerbosity(0);

        Logger.OnLog += (entry) => {
            if (_isWindowReady) {
                try {
                    _window?.Invoke(() => {
                        _window?.SendWebMessage(JsonSerializer.Serialize(new { type = "LOG", data = entry }));
                    });
                } catch { }
            }
        };

        _window.RegisterWindowCreatedHandler((s, a) => {
            Task.Run(async () => {
                try {
                    await _coordinator.InitializeAllAsync();
                    if (_settings.Config.AutoPatchOnStart) {
                        string wrapperPath = Path.Combine(baseDir, "tools", "redirector.exe");
                        if (File.Exists(wrapperPath)) patcherService.StartMonitoring(wrapperPath);
                    }
                } catch { }
            });
        });

        string indexPath = Path.Combine(baseDir, "wwwroot", "index.html");
        if (!File.Exists(indexPath)) {
            indexPath = Path.GetFullPath(Path.Combine(baseDir, "../../../src/WKVRCProxy.UI/ui/dist/index.html"));
        }

        if (File.Exists(indexPath)) _window.Load(indexPath);
        else _window.LoadRawString("UI Build Missing");

        _window.WaitForClose();
        OnShutdown();
    }

    private static void OnShutdown()
    {
        try {
            var patcher = _coordinator?.GetModule<PatcherService>();
            patcher?.Shutdown();
        } catch { }
        _coordinator?.Dispose();
        _logger?.Dispose();
    }

    private static void SetupHostsBypass()
    {
        try
        {
            string hostsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
            File.AppendAllText(hostsPath, "\r\n127.0.0.1 localhost.youtube.com\r\n");
            
            Process.Start(new ProcessStartInfo("ipconfig", "/flushdns") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
        }
        catch (Exception ex)
        {
            string crashLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "hosts_setup_error.log");
            File.WriteAllText(crashLog, "SETUP ERROR: " + ex.ToString());
        }
        Environment.Exit(0);
    }

    private static void HandleWebMessage(string message)
    {
        _isWindowReady = true; // UI is now alive and safe to receive messages
        try {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            string type = root.GetProperty("type").GetString() ?? "";

            switch (type) {
                case "EXIT": _window?.Close(); break;
                case "GET_CONFIG": _window?.SendWebMessage(JsonSerializer.Serialize(new { type = "CONFIG", data = _settings?.Config })); break;
                case "OPEN_BROWSER":
                    if (root.TryGetProperty("data", out var browserData)) {
                        string url = browserData.GetProperty("url").GetString() ?? "";
                        if (!string.IsNullOrEmpty(url)) Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                    }
                    break;
                case "SYNC_LOGS":
                    foreach (var entry in Logger.GetHistory())
                        _window?.SendWebMessage(JsonSerializer.Serialize(new { type = "LOG", data = entry }));
                    break;
                case "SAVE_CONFIG":
                    if (root.TryGetProperty("data", out var configData)) {
                        var newConfig = JsonSerializer.Deserialize<WKVRCProxy.Core.Models.AppConfig>(configData.GetRawText());
                        if (newConfig != null && _settings != null) {
                            _settings.Config.DebugMode = newConfig.DebugMode;
                            _settings.Config.PreferredResolution = newConfig.PreferredResolution;
                            _settings.Config.ForceIPv4 = newConfig.ForceIPv4;
                            _settings.Config.AutoPatchOnStart = newConfig.AutoPatchOnStart;
                            _settings.Config.CustomVrcPath = newConfig.CustomVrcPath;
                            _settings.Config.BypassHostsSetupDeclined = newConfig.BypassHostsSetupDeclined;
                            _settings.Save();
                        }
                    }
                    break;
                case "PICK_VRC_PATH":
                    _window?.Invoke(() => {
                        using var dialog = new CommonOpenFileDialog { IsFolderPicker = true };
                        if (dialog.ShowDialog() == CommonFileDialogResult.Ok && _settings != null) {
                            _settings.Config.CustomVrcPath = dialog.FileName;
                            _settings.Save();
                            _coordinator?.GetModule<PatcherService>().UpdateToolsDir(dialog.FileName);
                            _window?.SendWebMessage(JsonSerializer.Serialize(new { type = "CONFIG", data = _settings.Config }));
                        }
                    });
                    break;
                case "HOSTS_SETUP_ACCEPTED":
                    _coordinator?.GetModule<HostsManager>().HandleUserResponse(true);
                    break;
                case "HOSTS_SETUP_DECLINED":
                    _coordinator?.GetModule<HostsManager>().HandleUserResponse(false);
                    break;
                case "REQUEST_HOSTS_SETUP":
                    Task.Run(() => {
                        _coordinator?.GetModule<HostsManager>().RequestBypassAsync();
                    });
                    break;
            }
        } catch { }
    }
}
