using System;
using System.Collections.Generic;
using System.Threading;
using Dreambit.ECS;

namespace Dreambit.Events;

/// <summary>
///     Scene-level event dispatcher.
///     Resolve it through <see cref="Scene.Services" />.
/// </summary>
public sealed class EventBus : SceneServiceComponent
{
    private readonly object _listenerLock = new();

    /*
     * The concrete event type is the key.
     *
     * typeof(FireEvent)  -> Action<FireEventArgs>
     * typeof(DeathEvent) -> Action<DeathEventArgs>
     */
    private readonly Dictionary<Type, Delegate> _listeners = [];

    /// <summary>
    ///     Subscribes a typed listener to an event.
    /// </summary>
    /// <returns>
    ///     A disposable subscription that removes the listener when disposed.
    /// </returns>
    public IDisposable Subscribe<TArgs>(
        DreambitEvent<TArgs> dreambitEvent,
        Action<TArgs> listener)
        where TArgs : DreambitEventArgs
    {
        ArgumentNullException.ThrowIfNull(dreambitEvent);
        ArgumentNullException.ThrowIfNull(listener);

        ThrowIfUnavailable();

        var eventType = dreambitEvent.GetType();

        lock (_listenerLock)
        {
            if (_listeners.TryGetValue(eventType, out var existing))
            {
                if (existing is not Action<TArgs> typedListeners)
                    throw CreateSignatureMismatchException(
                        eventType,
                        typeof(TArgs),
                        existing.GetType());

                _listeners[eventType] = typedListeners + listener;
            }
            else
            {
                _listeners[eventType] = listener;
            }
        }

        return new EventSubscription(() => Unsubscribe(dreambitEvent, listener));
    }

    /// <summary>
    ///     Subscribes a parameterless listener.
    /// </summary>
    public IDisposable Subscribe(
        DreambitEvent<EmptyDreambitEventArgs> dreambitEvent,
        Action listener)
    {
        ArgumentNullException.ThrowIfNull(dreambitEvent);
        ArgumentNullException.ThrowIfNull(listener);

        Action<EmptyDreambitEventArgs> wrapper = _ => listener();

        return Subscribe(dreambitEvent, wrapper);
    }

    /// <summary>
    ///     Unsubscribes a typed listener.
    /// </summary>
    public void Unsubscribe<TArgs>(
        DreambitEvent<TArgs> dreambitEvent,
        Action<TArgs> listener)
        where TArgs : DreambitEventArgs
    {
        ArgumentNullException.ThrowIfNull(dreambitEvent);
        ArgumentNullException.ThrowIfNull(listener);

        var eventType = dreambitEvent.GetType();

        lock (_listenerLock)
        {
            if (!_listeners.TryGetValue(eventType, out var existing))
                return;

            if (existing is not Action<TArgs> typedListeners)
                throw CreateSignatureMismatchException(
                    eventType,
                    typeof(TArgs),
                    existing.GetType());

            var remainingListeners = typedListeners - listener;

            if (remainingListeners is null)
                _listeners.Remove(eventType);
            else
                _listeners[eventType] = remainingListeners;
        }
    }

    /// <summary>
    ///     Invokes an event and delivers its arguments to all listeners.
    /// </summary>
    public void Invoke<TArgs>(
        DreambitEvent<TArgs> dreambitEvent,
        TArgs eventArgs)
        where TArgs : DreambitEventArgs
    {
        ArgumentNullException.ThrowIfNull(dreambitEvent);
        ArgumentNullException.ThrowIfNull(eventArgs);

        ThrowIfUnavailable();

        var eventType = dreambitEvent.GetType();
        Action<TArgs> listeners;

        lock (_listenerLock)
        {
            if (!_listeners.TryGetValue(eventType, out var existing))
                return;

            if (existing is not Action<TArgs> typedListeners)
                throw CreateSignatureMismatchException(
                    eventType,
                    typeof(TArgs),
                    existing.GetType());

            /*
             * Copy the delegate before leaving the lock. This allows
             * listeners to subscribe or unsubscribe during invocation.
             */
            listeners = typedListeners;
        }

        InvokeListeners(eventType, listeners, eventArgs);
    }

    /// <summary>
    ///     Invokes an event that carries no data.
    /// </summary>
    public void Invoke(
        DreambitEvent<EmptyDreambitEventArgs> dreambitEvent)
    {
        Invoke(
            dreambitEvent,
            EmptyDreambitEventArgs.Instance);
    }

    /// <summary>
    ///     Removes every listener for one event.
    /// </summary>
    public void Clear<TArgs>(
        DreambitEvent<TArgs> dreambitEvent)
        where TArgs : DreambitEventArgs
    {
        ArgumentNullException.ThrowIfNull(dreambitEvent);

        lock (_listenerLock)
        {
            _listeners.Remove(dreambitEvent.GetType());
        }
    }

    /// <summary>
    ///     Removes every listener registered with this bus.
    /// </summary>
    public void ClearAll()
    {
        lock (_listenerLock)
        {
            _listeners.Clear();
        }
    }

    public override void OnDestroyed()
    {
        ClearAll();
    }

    protected override void OnServiceDisposing()
    {
        ClearAll();
        base.OnServiceDisposing();
    }

    private void InvokeListeners<TArgs>(
        Type eventType,
        Action<TArgs> listeners,
        TArgs eventArgs)
        where TArgs : DreambitEventArgs
    {
        /*
         * Calling listeners.Invoke(eventArgs) would stop at the first
         * exception. Invoking each listener individually allows the
         * remaining subscribers to still receive the event.
         */
        foreach (var listenerDelegate in listeners.GetInvocationList())
            try
            {
                ((Action<TArgs>)listenerDelegate).Invoke(eventArgs);
            }
            catch (Exception exception)
            {
                Logger.Error(
                    "Event listener failed.\n" +
                    $"Event: {eventType.FullName}\n" +
                    $"Listener: {listenerDelegate.Method.DeclaringType?.FullName}" +
                    $".{listenerDelegate.Method.Name}\n" +
                    $"Exception: {exception}");
            }
    }

    private void ThrowIfUnavailable()
    {
        if (IsDestroyed)
            throw new ObjectDisposedException(
                nameof(EventBus),
                "The EventBus component has been destroyed.");
    }

    private static InvalidOperationException
        CreateSignatureMismatchException(
            Type eventType,
            Type expectedArgsType,
            Type registeredDelegateType)
    {
        return new InvalidOperationException(
            $"Event '{eventType.FullName}' expects arguments of type " +
            $"'{expectedArgsType.FullName}', but its registered delegate " +
            $"uses '{registeredDelegateType.FullName}'.");
    }

    private sealed class EventSubscription : IDisposable
    {
        private Action _unsubscribe;

        public EventSubscription(Action unsubscribe)
        {
            _unsubscribe = unsubscribe;
        }

        public void Dispose()
        {
            Interlocked.Exchange(
                ref _unsubscribe,
                null)?.Invoke();
        }
    }
}
