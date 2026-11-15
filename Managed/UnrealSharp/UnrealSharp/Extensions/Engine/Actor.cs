using UnrealSharp.CoreUObject;
using UnrealSharp.EnhancedInput;
using UnrealSharp.UnrealSharpCore;

namespace UnrealSharp.Engine;

public partial class AActor
{
    /// <summary>
    /// All components of the actor
    /// </summary>
    public IList<UActorComponent> Components => GetComponentsByClass<UActorComponent>();

    /// <summary>
    /// If true, this actor will replicate to remote machines
    /// </summary>
    public bool Replicates
    {
        get => UCSActorExtensions.GetReplicates(this);
        set => UCSActorExtensions.SetReplicates(this, value);
    }
        
    /// <summary>
    /// Bind an action to a callback.
    /// </summary>
    /// <param name="actionName"> The name of the action to bind </param>
    /// <param name="inputEvent"> The input event to bind the action to </param>
    /// <param name="action"> The action to execute when the action is triggered </param>
    /// <param name="consumeInput"> Whether to consume the input </param>
    /// <param name="executeWhenPaused"> Whether to execute the action when paused </param>
    public void BindAction(string actionName, EInputEvent inputEvent, Action action, bool consumeInput = false, bool executeWhenPaused = false)
    {
        UInputComponent? inputComponent = InputComponent;
        if (inputComponent != null)
        {
            inputComponent.BindAction(actionName, inputEvent, action, consumeInput, executeWhenPaused);
        }
    }

    /// <summary>
    /// Binds an action to a callback.
    /// </summary>
    /// <param name="action"> The action to bind </param>
    /// <param name="triggerEvent"> The trigger event to bind the action to </param>
    /// <param name="callback"> The callback to execute when the action is triggered </param>
    public void BindAction(UInputAction action, ETriggerEvent triggerEvent, Action<FInputActionValue, float, float, UInputAction> callback)
    {
        if (InputComponent is UEnhancedInputComponent enhancedInputComponent)
        {
            enhancedInputComponent.BindAction(action, triggerEvent, callback);
        }
    }

    /// <summary>
    /// Binds an axis to an action.
    /// </summary>
    /// <param name="axisName"> The name of the axis to bind </param>
    /// <param name="action"> The action to bind the axis to </param>
    /// <param name="consumeInput"> Whether to consume the input </param>
    /// <param name="executeWhenPaused"> Whether to execute the action when paused </param>
    public void BindAxis(string axisName, Action<float> action, bool consumeInput = false, bool executeWhenPaused = false)
    {
        UInputComponent? inputComponent = InputComponent;
        
        if (inputComponent != null)
        {
            inputComponent.BindAxis(axisName, action, consumeInput, executeWhenPaused);
        }
    }

    /// <summary>
    /// Adds a component to the actor by class.
    /// </summary>
    /// <param name="bManualAttachment">Whether to manually attach the component</param>
    /// <param name="relativeTransform">Set the relative transform of the component</param>
    /// <typeparam name="T">Class of the component to add</typeparam>
    /// <returns>The component</returns>
    public T AddComponentByClass<T>(bool bManualAttachment, FTransform relativeTransform) where T : UActorComponent
        => (AddComponentByClass(new TSubclassOf<UActorComponent>(typeof(T)), bManualAttachment, relativeTransform, deferredFinish: false) as T)!;

    /// <summary>
    /// Adds a component to the actor by class.
    /// </summary>
    /// <param name="bManualAttachment">Whether to manually attach the component</param>
    /// <param name="relativeTransform">Set the relative transform of the component</param>
    /// <param name="initializerFunc"> The function to initialize the component </param>
    /// <typeparam name="T">Class of the component to add</typeparam>
    /// <returns>The component</returns>
    public T AddComponentByClass<T>(bool bManualAttachment, FTransform relativeTransform, Action<T> initializerFunc) where T : UActorComponent
    {
        T component = (AddComponentByClass(new TSubclassOf<UActorComponent>(typeof(T)), bManualAttachment, relativeTransform, deferredFinish: true) as T)!;
        initializerFunc(component);
        FinishAddComponent(component, bManualAttachment, relativeTransform);
        return component;
    }

    /// <summary>
    /// Adds a component to the actor by class.
    /// </summary>
    /// <param name="class">Class of the component to get</param>
    /// <param name="bManualAttachment">Whether to manually attach the component</param>
    /// <param name="relativeTransform">Set the relative transform of the component</param>
    /// <param name="initializerFunc"> The function to initialize the component </param>
    /// <typeparam name="T">Class of the component to add</typeparam>
    /// <returns>The component</returns>
    public T AddComponentByClass<T>(TSubclassOf<T> @class, bool bManualAttachment, FTransform relativeTransform, Action<T> initializerFunc) where T : UActorComponent
    {
        T component = (AddComponentByClass(@class.Cast<UActorComponent>(), bManualAttachment, relativeTransform, deferredFinish: true) as T)!;
        initializerFunc(component);
        FinishAddComponent(component, bManualAttachment, relativeTransform);
        return component;
    }

    /// <summary>
    /// Adds a component to the actor by class.
    /// </summary>
    /// <param name="class">Class of the component to get</param>
    /// <param name="bManualAttachment">Whether to manually attach the component</param>
    /// <param name="relativeTransform">Set the relative transform of the component</param>
    /// <typeparam name="T">Class of the component to add</typeparam>
    /// <returns>The component if added, otherwise null</returns>
    public T AddComponentByClass<T>(TSubclassOf<T> @class, bool bManualAttachment, FTransform relativeTransform) where T : UActorComponent
        => (AddComponentByClass(@class.Cast<UActorComponent>(), bManualAttachment, relativeTransform, deferredFinish: false) as T)!;

    
    

    /// <summary>
    /// Move the Actor to the specified location.
    /// </summary>
    public bool SetActorLocation(FVector newLocation)
    {
        return SetActorLocation(newLocation, false, out _, false);
    }
    
    /// <summary>
    /// Set the Actor's rotation instantly to the specified rotation.
    /// </summary>
    public bool SetActorRotation(FRotator newRotation)
    {
        return SetActorRotation(newRotation, false);
    }
    
    /// <summary>
    /// Set the Actors transform to the specified one.
    /// </summary>
    public bool SetActorTransform(FTransform newTransform)
    {
        return SetActorTransform(newTransform, false, out _, false);
    }
    
    /// <summary>
    /// Move the actor instantly to the specified location and rotation.
    /// </summary>
    public bool SetActorLocationRotation(FVector newLocation, FRotator newRotation)
    {
        return SetActorLocationAndRotation(newLocation, newRotation, false, out _, false);
    }
    
    /// <summary>
    /// Returns the world space bounding box of all components in this Actor.
    /// </summary>
    /// <param name="bNonColliding">Indicates that you want to include non-colliding components in the bounding box</param>
    /// <param name="bIncludeFromChildActors">If true then recurse in to ChildActor components and find components of the appropriate type in those Actors as well</param>
    /// <returns>The bounding box of all components in this actor</returns>
    public FBox GetComponentsBoundingBox(bool bNonColliding = false, bool bIncludeFromChildActors = false)
    {
        return UCSActorExtensions.GetComponentsBoundingBox(this, bNonColliding, bIncludeFromChildActors);
    }
    
    /// <summary>
    /// Iterates over all components of the specified class and executes the action.
    /// </summary>
    /// <param name="action">The action to execute</param>
    /// <typeparam name="T">The type of the component</typeparam>
    public void ForEachComponent<T>(Action<T> action) where T : UActorComponent
    {
        IList<T> components = GetComponentsByClass<T>();
        
        foreach (T component in components)
        {
            action(component);
        }
    }
    
    /// <summary>
    /// Iterates over all components of the actor
    /// </summary>
    public void ForEachComponent(Action<UActorComponent> action)
    {
        ForEachComponent<UActorComponent>(action);
    }
    
    /// <summary>
    /// Allows this actor to be net-addressable by full path name, even if the actor was spawned after map load.
    /// The caller is required to ensure that this actor's name is stable between server/client. Must be called before FinishSpawning
    /// </summary>
    public void MarkAsNetworkAddressable() => UCSActorExtensions.MarkAsNetworkAddressable(this);
}
