using System;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;

namespace WKVRCProxy.Core;

public interface IProxyModule
{
    string Name { get; }
    Task InitializeAsync(IModuleContext context);
    void Shutdown();

    ModuleHealthReport GetHealthReport() => new ModuleHealthReport
    {
        ModuleName = Name,
        Status = HealthStatus.Healthy,
        Reason = "",
        LastChecked = DateTime.Now
    };
}
