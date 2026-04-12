using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core.Services;

public class RelayPortManager : IProxyModule
{
    public string Name => "RelayPortManager";
    public int CurrentPort { get; private set; }
    
    private Logger? _logger;
    private string? _portFile;

    public Task InitializeAsync(IModuleContext context)
    {
        _logger = context.Logger;

        RefreshPort();

        return Task.CompletedTask;
    }

    public void RefreshPort()
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            CurrentPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            
            _logger?.Debug($"Assigned ephemeral relay port: {CurrentPort}");
            
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WKVRCProxy");
            if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);
            
            _portFile = Path.Combine(appData, "relay_port.dat");
            
            using (var fs = new FileStream(_portFile, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(fs))
            {
                writer.Write(CurrentPort.ToString());
            }

            _logger?.Success($"Relay port exported: {CurrentPort}");
        }
        catch (Exception ex)
        {
            _logger?.Error("Failed to initialize or export relay port: " + ex.Message);
            CurrentPort = 0;
        }
    }

    public ModuleHealthReport GetHealthReport()
    {
        return new ModuleHealthReport
        {
            ModuleName = Name,
            Status = CurrentPort > 0 ? HealthStatus.Healthy : HealthStatus.Failed,
            Reason = CurrentPort > 0 ? "" : "Failed to bind relay port",
            LastChecked = DateTime.Now
        };
    }

    public void Shutdown()
    {
        if (!string.IsNullOrEmpty(_portFile) && File.Exists(_portFile))
        {
            try
            {
                File.Delete(_portFile);
            }
            catch (Exception ex)
            {
                _logger?.Warning("Failed to cleanup relay port file: " + ex.Message);
            }
        }
    }
}
