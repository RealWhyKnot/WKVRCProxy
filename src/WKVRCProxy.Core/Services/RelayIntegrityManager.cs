using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

public class RelayIntegrityManager : IProxyModule, IDisposable
{
    public string Name => "RelayIntegrity";
    
    private Logger? _logger;
    private IModuleContext? _context;
    private SystemEventBus? _eventBus;
    private readonly CancellationTokenSource _cts = new();
    private bool _lastCheckPassed = true;

    public Task InitializeAsync(IModuleContext context)
    {
        _context = context;
        _logger = context.Logger;
        _eventBus = context.EventBus;

        _ = Task.Run(MonitorIntegrityLoop, _cts.Token);
        
        return Task.CompletedTask;
    }

    private async Task MonitorIntegrityLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var ips = await Dns.GetHostAddressesAsync("localhost.youtube.com");
                bool isBypassed = ips.Any(ip => ip.ToString() == "127.0.0.1");
                _lastCheckPassed = isBypassed;

                if (!isBypassed)
                {
                    _logger?.Error("DNS Bypass broken. Public worlds will fail. Please fix your hosts file.");
                    _eventBus?.PublishError("RelayIntegrity", new ErrorContext {
                        Category = ErrorCategory.Network,
                        Code = ErrorCodes.DNS_BYPASS_BROKEN,
                        Summary = "DNS bypass is broken",
                        Detail = "localhost.youtube.com does not resolve to 127.0.0.1",
                        ActionHint = "Run the hosts file setup from Settings, or manually add '127.0.0.1 localhost.youtube.com' to your hosts file",
                        IsRecoverable = true
                    });
                }
            }
            catch (Exception ex)
            {
                // DNS lookup failed — expected when offline or DNS is unreachable; trace only to avoid noise
                _logger?.Debug("DNS integrity check failed (may be offline): " + ex.Message);
            }
            
            await Task.Delay(30000, _cts.Token);
        }
    }

    public ModuleHealthReport GetHealthReport()
    {
        return new ModuleHealthReport
        {
            ModuleName = Name,
            Status = _lastCheckPassed ? HealthStatus.Healthy : HealthStatus.Degraded,
            Reason = _lastCheckPassed ? "" : "DNS bypass appears broken -- localhost.youtube.com does not resolve to 127.0.0.1",
            LastChecked = DateTime.Now
        };
    }

    public void Shutdown()
    {
        _cts.Cancel();
    }

    public void Dispose()
    {
        Shutdown();
        _cts.Dispose();
    }
}
