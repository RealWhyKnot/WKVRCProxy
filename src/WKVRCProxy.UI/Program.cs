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
using WKVRCProxy.Core.Diagnostics;
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
        _settings.SetLogger(_logger); // inject after construction — breaks circular dep

        _coordinator = new ModuleCoordinator(_logger, _settings);

        // Wire logger into the centralized event bus
        _logger.SetEventBus(_coordinator.EventBus);

        var logMonitor = new VrcLogMonitor();
        var codecInstaller = new CodecInstaller();
        var patcherService = new PatcherService();
        var ipcServer = new WebSocketIpcServer();
        var tier2Client = new Tier2WebSocketClient(_logger);
        var hostsManager = new HostsManager();
        var relayPortManager = new RelayPortManager();
        var proxyRuleManager = new ProxyRuleManager();
        var relayServer = new RelayServer();
        var curlClient = new CurlImpersonateClient();
        var potProvider = new PotProviderService();
        var integrityManager = new RelayIntegrityManager();

        _coordinator.Register(logMonitor);
        _coordinator.Register(codecInstaller);
        _coordinator.Register(patcherService);
        _coordinator.Register(ipcServer);
        _coordinator.Register(tier2Client);
        _coordinator.Register(hostsManager);
        _coordinator.Register(relayPortManager);
        _coordinator.Register(proxyRuleManager);
        _coordinator.Register(curlClient);
        _coordinator.Register(potProvider);
        _coordinator.Register(relayServer);
        _coordinator.Register(integrityManager);

        // Keep legacy event handlers for backward compatibility during transition.
        // These will be removed once all UI communication moves through the event bus.
        hostsManager.OnIpcRequest += (type, data) => {
            if (_isWindowReady) {
                try {
                    _window?.Invoke(() => {
                        _window?.SendWebMessage(JsonSerializer.Serialize(new { type = type, data = data }));
                    });
                } catch (Exception ex) {
                    _logger?.Warning("IPC send to UI failed: " + ex.Message, ex);
                }
            }
            else {
                Task.Run(async () => {
                    while (!_isWindowReady) await Task.Delay(200);
                    _window?.Invoke(() => {
                        _window?.SendWebMessage(JsonSerializer.Serialize(new { type = type, data = data }));
                    });
                });
            }
        };

        relayServer.OnRelayEvent += (relayEvent) => {
            if (_isWindowReady) {
                try {
                    _window?.Invoke(() => {
                        _window?.SendWebMessage(JsonSerializer.Serialize(new { type = "RELAY_EVENT", data = relayEvent }));
                    });
                } catch (Exception ex) {
                    _logger?.Warning("RelayEvent send to UI failed: " + ex.Message, ex);
                }
            }
        };

        var resEngine = new ResolutionEngine(_logger, _settings, logMonitor, tier2Client, hostsManager, relayPortManager, patcherService, curlClient, potProvider);
        resEngine.SetEventBus(_coordinator.EventBus);

        ipcServer.OnResolveRequested += async (payload) => await resEngine.ResolveAsync(payload);
        logMonitor.OnVrcPathDetected += (path) => {
            patcherService.UpdateToolsDir(path);
            ipcServer.ExportPortToDirectory(path);
        };

        resEngine.OnStatusUpdate += (msg, stats) => {
            if (_isWindowReady) {
                try {
                    _window?.Invoke(() => {
                        _window?.SendWebMessage(JsonSerializer.Serialize(new { type = "STATUS", data = new { message = msg, stats = stats } }));
                    });
                } catch (Exception ex) {
                    _logger?.Warning("Status event send to UI failed: " + ex.Message, ex);
                }
            }
        };

        // Centralized event bus subscription — single point for all events to UI
        _coordinator.EventBus.OnEvent += (evt) => {
            if (!_isWindowReady) return;
            try {
                string eventType;
                object data;
                switch (evt.Type) {
                    case SystemEventType.Health:
                        eventType = "HEALTH";
                        data = evt.Payload!;
                        break;
                    case SystemEventType.Error:
                        eventType = "ERROR";
                        data = evt.Payload!;
                        break;
                    default:
                        return; // Log, Status, Relay, Prompt handled by legacy events for now
                }
                _window?.Invoke(() => {
                    _window?.SendWebMessage(JsonSerializer.Serialize(
                        new { type = eventType, data = data, correlationId = evt.CorrelationId, source = evt.SourceModule }));
                });
            } catch (Exception ex) {
                System.Diagnostics.Debug.WriteLine("Event bus -> UI dispatch error: " + ex.Message);
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
                    RunPreflightChecks(baseDir);
                    await _coordinator.InitializeAllAsync();
                    // If VRChat Tools path was already known at startup, export the IPC port
                    // there immediately so the redirector can find it without needing a log event.
                    string? knownToolsDir = patcherService.VrcToolsDir;
                    if (!string.IsNullOrEmpty(knownToolsDir))
                        ipcServer.ExportPortToDirectory(knownToolsDir);
                    if (_settings.Config.AutoPatchOnStart) {
                        string wrapperPath = Path.Combine(baseDir, "tools", "redirector.exe");
                        if (File.Exists(wrapperPath)) patcherService.StartMonitoring(wrapperPath);
                    }

                    // Start periodic health broadcast (every 10 seconds)
                    _ = Task.Run(async () => {
                        while (true)
                        {
                            await Task.Delay(10000);
                            if (_isWindowReady && _coordinator != null)
                            {
                                try
                                {
                                    var health = _coordinator.GetSystemHealth();
                                    foreach (var report in health)
                                        _coordinator.EventBus.PublishHealth(report);
                                }
                                catch { /* Health check itself shouldn't crash the app */ }
                            }
                        }
                    });
                } catch (Exception ex) {
                    _logger?.Fatal("Coordinator initialization failed: " + ex.Message, ex);
                }
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

    private static void RunPreflightChecks(string baseDir)
    {
        string toolsDir = Path.Combine(baseDir, "tools");

        string redirector = Path.Combine(toolsDir, "redirector.exe");
        if (!File.Exists(redirector))
        {
            _logger?.Fatal("PREFLIGHT: redirector.exe missing from tools/ — patching is disabled. Reinstall or rebuild.");
            _coordinator?.EventBus.PublishError("Preflight", new ErrorContext {
                Category = ErrorCategory.FileSystem,
                Code = ErrorCodes.REDIRECTOR_MISSING,
                Summary = "redirector.exe missing from tools/",
                Detail = "Patching is disabled without this file",
                ActionHint = "Reinstall or rebuild WKVRCProxy",
                IsRecoverable = false
            });
        }

        string ytdlp = Path.Combine(toolsDir, "yt-dlp.exe");
        if (!File.Exists(ytdlp))
        {
            _logger?.Warning("PREFLIGHT: yt-dlp.exe missing from tools/ — Tier 1 resolution will fail.");
            _coordinator?.EventBus.PublishError("Preflight", new ErrorContext {
                Category = ErrorCategory.FileSystem,
                Code = ErrorCodes.YTDLP_MISSING,
                Summary = "yt-dlp.exe missing from tools/",
                Detail = "Tier 1 resolution will fail without this file",
                ActionHint = "Reinstall or rebuild WKVRCProxy",
                IsRecoverable = true
            });
        }

        string defaultVrcTools = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
            "VRChat", "VRChat", "Tools");
        bool hasCustomPath = !string.IsNullOrEmpty(_settings?.Config.CustomVrcPath);
        if (!Directory.Exists(defaultVrcTools) && !hasCustomPath)
            _logger?.Warning("PREFLIGHT: VRChat Tools folder not found. Launch VRChat at least once, or set a custom path in Settings.");
    }

    private static void OnShutdown()
    {
        try {
            var patcher = _coordinator?.GetModule<PatcherService>();
            patcher?.Shutdown();
        } catch (Exception ex) {
            _logger?.Warning("Shutdown error: " + ex.Message, ex);
        }
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
                            _settings.Config.EnableRelayBypass = newConfig.EnableRelayBypass;
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
                case "ADD_FIREWALL_RULE":
                    Task.Run(() => {
                        try
                        {
                            var psi = new ProcessStartInfo {
                                FileName = "netsh",
                                Arguments = "advfirewall firewall add rule name=\"WKVRCProxy Relay\" dir=in action=allow program=\"" + Process.GetCurrentProcess().MainModule?.FileName + "\" enable=yes",
                                Verb = "runas",
                                UseShellExecute = true,
                                WindowStyle = ProcessWindowStyle.Hidden
                            };
                            Process.Start(psi)?.WaitForExit();
                            _logger?.Success("Firewall exclusion rule added successfully.");
                        }
                        catch (Exception ex)
                        {
                            _logger?.Error("Failed to add firewall rule: " + ex.Message);
                        }
                    });
                    break;
                case "GET_HEALTH":
                    if (_coordinator != null)
                    {
                        var health = _coordinator.GetSystemHealth();
                        _window?.SendWebMessage(JsonSerializer.Serialize(new {
                            type = "HEALTH",
                            data = health,
                            overall = _coordinator.GetOverallHealth().ToString()
                        }));
                    }
                    break;
            }
        } catch (Exception ex) {
            _logger?.Warning("WebMessage parse error: " + ex.Message, ex);
        }
    }
}
