using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

public class RelayIntegrityManager : IProxyModule, IDisposable
{
    public string Name => "RelayIntegrity";
    
    private Logger? _logger;
    private IModuleContext? _context;
    private readonly CancellationTokenSource _cts = new();

    public Task InitializeAsync(IModuleContext context)
    {
        _context = context;
        _logger = context.Logger;
        _logger.Trace("Initializing RelayIntegrityManager...");
        
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
                
                if (!isBypassed)
                {
                    _logger?.Error("DNS Bypass broken. Public worlds will fail. Please fix your hosts file.");
                }
            }
            catch (Exception ex)
            {
                // DNS lookup failed — expected when offline or DNS is unreachable; trace only to avoid noise
                _logger?.Trace("DNS integrity check failed (may be offline): " + ex.Message);
            }
            
            await Task.Delay(30000, _cts.Token);
        }
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
