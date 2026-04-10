using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WKVRCProxy.Core.Logging;

namespace WKVRCProxy.Core;

public interface IModuleContext
{
    Logger Logger { get; }
    SettingsManager Settings { get; }
    T GetModule<T>() where T : class, IProxyModule;
}

public class ModuleCoordinator : IModuleContext, IDisposable
{
    private readonly List<IProxyModule> _modules = new();
    public Logger Logger { get; }
    public SettingsManager Settings { get; }

    public ModuleCoordinator(Logger logger, SettingsManager settings)
    {
        Logger = logger;
        Settings = settings;
    }

    public void Register(IProxyModule module)
    {
        _modules.Add(module);
        Logger.Trace("Module Registered: " + module.Name);
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
        foreach (var m in _modules)
        {
            try {
                Logger.Trace("Initializing " + m.Name + "...");
                await m.InitializeAsync(this);
                Logger.Success(m.Name + " ready.");
            } catch (Exception ex) {
                Logger.Fatal(m.Name + " Failed to Init: " + ex.Message, ex);
            }
        }
        Logger.Info("Subsystems Initialized.");
    }

    public void ShutdownAll()
    {
        Logger.Info("Shutting down subsystems...");
        foreach (var m in _modules)
        {
            try { m.Shutdown(); }
            catch (Exception ex) { Logger.Trace(m.Name + " shutdown error: " + ex.Message, ex); }
        }
    }

    public void Dispose()
    {
        ShutdownAll();
    }
}
