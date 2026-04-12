using System;

namespace WKVRCProxy.Core.Diagnostics;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class DependsOnAttribute : Attribute
{
    public Type ModuleType { get; }
    public bool IsCritical { get; }

    public DependsOnAttribute(Type moduleType, bool critical = false)
    {
        ModuleType = moduleType;
        IsCritical = critical;
    }
}
