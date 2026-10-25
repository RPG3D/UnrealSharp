using System.Diagnostics;
using System.Runtime.Loader;

namespace UnrealSharp.StaticVars;

public class FBaseStaticVar<T>
{
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private T? _value;
    
    public virtual T? Value
    {
        get => _value;
        set => _value = value;
    }
    
#if WITH_EDITOR
    public FBaseStaticVar()
    {
        // Unified for Mono and CoreCLR: collectible ALC Unloading fires on both backends
        // with this MonoSDK (verified empirically on mono 10.0.13). The handler
        // unsubscribes itself when the ALC unloads.
        AssemblyLoadContext alc = AssemblyLoadContext.GetLoadContext(GetType().Assembly)!;
        alc.Unloading += OnAlcUnloading;
    }

    protected virtual void OnAlcUnloading(AssemblyLoadContext alc)
    {
        alc.Unloading -= OnAlcUnloading;
    }
#endif
    
    public override string ToString()
    {
        var value = Value;
        if (value == null)
        {
            return "null value";
        }
        
        return value.ToString()!;
    }
}