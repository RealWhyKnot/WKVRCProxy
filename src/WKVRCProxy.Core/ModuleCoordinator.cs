using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using WKVRCProxy.Core.Diagnostics;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core;

public interface IModuleContext
{
    Logger Logger { get; }
    SettingsManager Settings { get; }
    SystemEventBus EventBus { get; }
    T GetModule<T>() where T : class, IProxyModule;
}

public class ModuleCoordinator : IModuleContext, IDisposable
{
    private readonly List<IProxyModule> _modules = new();
    private readonly HashSet<string> _failedModules = new();
    public Logger Logger { get; }
    public SettingsManager Settings { get; }
    public SystemEventBus EventBus { get; }

    public ModuleCoordinator(Logger logger, SettingsManager settings)
    {
        Logger = logger;
        Settings = settings;
        EventBus = new SystemEventBus();
    }

    public void Register(IProxyModule module)
    {
        _modules.Add(module);
    }

    public T GetModule<T>() where T : class, IProxyModule
    {
        foreach (var m in _modules)
        {
            if (m is T typed) return typed;
        }
        throw new Exception("Module " + typeof(T).Name + " not found.");
    }

    // Alias for compatibility if needed elsewhere
    public T Get<T>() where T : class, IProxyModule => GetModule<T>();

    public async Task InitializeAllAsync()
    {
        Logger.Info("Initializing Subsystems...");

        // Topological sort: modules with [DependsOn] attributes are ordered after their dependencies
        var ordered = TopologicalSort(_modules);

        foreach (var m in ordered)
        {
            // Check if any critical dependency failed
            bool skipDueToFailedDep = false;
            var deps = m.GetType().GetCustomAttributes(typeof(DependsOnAttribute), false);
            foreach (DependsOnAttribute dep in deps)
            {
                var depModule = GetModuleByType(dep.ModuleType);
                if (dep.IsCritical && depModule != null && _failedModules.Contains(depModule.Name))
                {
                    Logger.Warning(m.Name + " skipped: critical dependency " + depModule.Name + " failed to initialize.");
                    _failedModules.Add(m.Name);
                    skipDueToFailedDep = true;
                    break;
                }
            }
            if (skipDueToFailedDep) continue;

            try {
                await m.InitializeAsync(this);
                Logger.Success(m.Name + " ready.");
            } catch (Exception ex) {
                Logger.Fatal(m.Name + " Failed to Init: " + ex.Message, ex);
                _failedModules.Add(m.Name);
            }
        }
        Logger.Info("Subsystems Initialized.");
    }

    private IProxyModule? GetModuleByType(Type type)
    {
        foreach (var m in _modules)
        {
            if (type.IsAssignableFrom(m.GetType())) return m;
        }
        return null;
    }

    private List<IProxyModule> TopologicalSort(List<IProxyModule> modules)
    {
        // Build a map of type -> module for dependency resolution
        var typeToModule = new Dictionary<Type, IProxyModule>();
        foreach (var m in modules)
        {
            typeToModule[m.GetType()] = m;
        }

        var sorted = new List<IProxyModule>();
        var visited = new HashSet<IProxyModule>();
        var visiting = new HashSet<IProxyModule>();

        foreach (var m in modules)
        {
            Visit(m, typeToModule, sorted, visited, visiting);
        }

        return sorted;
    }

    private void Visit(IProxyModule module, Dictionary<Type, IProxyModule> typeToModule,
        List<IProxyModule> sorted, HashSet<IProxyModule> visited, HashSet<IProxyModule> visiting)
    {
        if (visited.Contains(module)) return;
        if (visiting.Contains(module))
        {
            Logger.Warning("Circular dependency detected involving " + module.Name + " — using registration order.");
            return;
        }

        visiting.Add(module);

        var deps = module.GetType().GetCustomAttributes(typeof(DependsOnAttribute), false);
        foreach (DependsOnAttribute dep in deps)
        {
            if (typeToModule.TryGetValue(dep.ModuleType, out var depModule))
            {
                Visit(depModule, typeToModule, sorted, visited, visiting);
            }
        }

        visiting.Remove(module);
        visited.Add(module);
        sorted.Add(module);
    }

    public ModuleHealthReport[] GetSystemHealth()
    {
        var reports = new List<ModuleHealthReport>();
        foreach (var m in _modules)
        {
            try
            {
                reports.Add(m.GetHealthReport());
            }
            catch
            {
                reports.Add(new ModuleHealthReport {
                    ModuleName = m.Name,
                    Status = HealthStatus.Failed,
                    Reason = "Health check threw an exception",
                    LastChecked = DateTime.Now
                });
            }
        }
        return reports.ToArray();
    }

    public HealthStatus GetOverallHealth()
    {
        var reports = GetSystemHealth();
        if (reports.Any(r => r.Status == HealthStatus.Failed)) return HealthStatus.Failed;
        if (reports.Any(r => r.Status == HealthStatus.Degraded)) return HealthStatus.Degraded;
        return HealthStatus.Healthy;
    }

    public void ShutdownAll()
    {
        Logger.Info("Shutting down subsystems...");
        foreach (var m in _modules)
        {
            try { m.Shutdown(); }
            catch (Exception ex) { Logger.Warning(m.Name + " shutdown error: " + ex.Message, ex); }
        }
    }

    public void Dispose()
    {
        ShutdownAll();
    }
}
