using UnrealSharp.CoreUObject;
using UnrealSharp.UnrealSharpCore;

namespace UnrealSharp.Engine;

public partial class UActorComponent
{
    /// <summary>
    /// Whether this component is replicated to clients.
    /// </summary>
    public bool IsReplicated 
    {
        get => UCSActorComponentExtensions.GetIsReplicated(this);
        set => UCSActorComponentExtensions.SetIsReplicated(this, value);
    }
    
    
    
}