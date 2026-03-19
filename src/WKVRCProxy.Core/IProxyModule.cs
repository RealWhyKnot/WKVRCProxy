using System.Threading.Tasks;

namespace WKVRCProxy.Core;

public interface IProxyModule
{
    string Name { get; }
    Task InitializeAsync(IModuleContext context);
    void Shutdown();
}
