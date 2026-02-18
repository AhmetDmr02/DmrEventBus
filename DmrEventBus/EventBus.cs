using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace DmrEventBus
{
    public static class EventBus
    {
        //This is creating strong reference indirectly to the target object.
        //But it turns out even with the reflection and weak reference usage, unity doesn't let plain c# objects to be garbage collected consistently.
        //Instead of relying on GC, the plain C# object must be unsubscribed manually inside its Dispose method.
        //Or should use IChildEventListener which returns a parent unity object and the class is automatically unsubscribed when the parent unity object is destroyed.
        private static readonly ConcurrentDictionary<Type, IEventRepository> _typeToEventDelegates = new();

        private static readonly ConcurrentDictionary<(object sender, Delegate original, Type type), Delegate> _wrapperMap
            = new ConcurrentDictionary<(object, Delegate, Type), Delegate>();

        private static readonly ConcurrentDictionary<object, ConcurrentDictionary<(Delegate action, Type eventType), byte>> _subscribedClassHashMap
            = new ConcurrentDictionary<object, ConcurrentDictionary<(Delegate, Type), byte>>();

        private static readonly ConcurrentDictionary<(object sender, Type eventType), ConcurrentDictionary<Delegate, byte>> _senderTypeIndex
            = new ConcurrentDictionary<(object, Type), ConcurrentDictionary<Delegate, byte>>();

        private static readonly object _writeLock = new();
        private static readonly ConcurrentQueue<object> _deadObjects = new ConcurrentQueue<object>();
        private static readonly int DEAD_OBJECT_CLEANUP_BATCH_SIZE = 50;

        public static void Subscribe<T>(object sender, Action<T> action)
        {
            //Scriptable objects will be called even if their subscription set from monobehaviour and the game object is destroyed
            //They need to be manually unsubscribed when the game object is destroyed
            if (sender is not MonoBehaviour && sender is not IDisposable && sender is not ScriptableObject)
            {
                Debug.LogWarning($"EventBus.Subscribe<T>(): No event is registered for type {typeof(T).Name} by {sender.GetType().Name} because it is not a MonoBehaviour, ScriptableObject or a IDisposable.");
                return;
            }

            lock (_writeLock)
            {
                var senderEvents = _subscribedClassHashMap.GetOrAdd(sender, _ => new ConcurrentDictionary<(Delegate, Type), byte>());

                if (senderEvents.ContainsKey((action, typeof(T))))
                {
                    Debug.LogWarning($"EventBus.Subscribe<T>(): Event {typeof(T).Name} is already subscribed to by {sender.GetType().Name}.");
                    return;
                }

                Action<T> wrappedAction;
                bool isMono = sender is MonoBehaviour;
                IChildEventListener childListener = sender as IChildEventListener;

                if (isMono)
                {
                    wrappedAction = (T e) =>
                    {
                        if (IsMainThread() && IsUnityObjectDead(sender))
                        {
                            _deadObjects.Enqueue(sender);
                            return;
                        }
                        else if (!IsMainThread())
                        {
                            Debug.LogWarning($"EventBus The MonoBehaviour {sender.GetType().Name} called on a non-main thread.");
                        }
                        action(e);
                    };
                }
                else if (childListener != null)
                {
                    wrappedAction = (T e) =>
                    {
                        if (IsMainThread() && IsUnityObjectDead(childListener.ReturnParentGameObject()))
                        {
                            _deadObjects.Enqueue(sender);
                            return;
                        }
                        action(e);
                    };
                }
                else
                {
                    wrappedAction = (T e) => action(e);
                }

                var repo = (EventRepository<T>)_typeToEventDelegates.GetOrAdd(typeof(T), _ => new EventRepository<T>());

                if (_wrapperMap.ContainsKey((sender, action, typeof(T)))) return;

                repo.Add(wrappedAction);

                _wrapperMap[(sender, action, typeof(T))] = wrappedAction;

                senderEvents[(action, typeof(T))] = 0;

                var typeSet = _senderTypeIndex.GetOrAdd((sender, typeof(T)), _ => new ConcurrentDictionary<Delegate, byte>());
                typeSet.TryAdd(action, 0);
            }
        }

        public static void UnsubscribeAll<T>(object sender)
        {
            lock (_writeLock)
            {
                var key = (sender, typeof(T));
                if (_senderTypeIndex.TryRemove(key, out var methodSet))
                {
                    if (_typeToEventDelegates.TryGetValue(typeof(T), out var baseRepo))
                    {
                        var repo = (EventRepository<T>)baseRepo;
                        foreach (var originalAction in methodSet.Keys)
                        {
                            if (_wrapperMap.TryRemove((sender, originalAction, typeof(T)), out var wrappedDelegate))
                            {
                                repo.Remove((Action<T>)wrappedDelegate);

                                // Clean main hash map
                                if (_subscribedClassHashMap.TryGetValue(sender, out var senderEvents))
                                {
                                    senderEvents.TryRemove((originalAction, typeof(T)), out _);
                                }
                            }
                        }
                    }
                    if (_subscribedClassHashMap.TryGetValue(sender, out var evs) && evs.IsEmpty)
                        _subscribedClassHashMap.TryRemove(sender, out _);
                }
            }
        }

        public static void Unsubscribe<T>(object sender, Action<T> action)
        {
            lock (_writeLock)
            {
                // We use the wrapper map as the primary check. 
                // If it's not here, event was never subscribed (or already unsubscribed).
                if (_wrapperMap.TryRemove((sender, action, typeof(T)), out var wrappedDelegate))
                {
                    if (_typeToEventDelegates.TryGetValue(typeof(T), out var baseRepo))
                    {
                        var repo = (EventRepository<T>)baseRepo;
                     
                        repo.Remove((Action<T>)wrappedDelegate);
                    }

                    if (_senderTypeIndex.TryGetValue((sender, typeof(T)), out var methodSet))
                    {
                        methodSet.TryRemove(action, out _);
                        if (methodSet.IsEmpty) _senderTypeIndex.TryRemove((sender, typeof(T)), out _);
                    }

                    if (_subscribedClassHashMap.TryGetValue(sender, out var senderEvents))
                    {
                        senderEvents.TryRemove((action, typeof(T)), out _);
                        if (senderEvents.IsEmpty) _subscribedClassHashMap.TryRemove(sender, out _);
                    }
                }
                else
                {
                    Debug.LogWarning($"EventBus.Unsubscribe<{typeof(T).Name}>(): no subscription found for {sender.GetType().Name}.");
                }
            }
        }

        public static void Publish<T>(T @event)
        {
            if (!_deadObjects.IsEmpty) CleanupDeadEvents();

            if (_typeToEventDelegates.TryGetValue(typeof(T), out var baseRepo))
            {
                var repo = baseRepo as EventRepository<T>;

                repo.Publish(@event);
            }
        }

        #region Helpers
        private static bool IsUnityObjectDead(object obj)
        {
            if (obj == null) return true;
            if (obj is UnityEngine.Object u) return u == null;
            return false;
        }

        private static void CleanupDeadEvents()
        {
            lock (_writeLock)
            {
                for (int i = 0; i < DEAD_OBJECT_CLEANUP_BATCH_SIZE; i++)
                {
                    if (_deadObjects.TryDequeue(out var deadSender))
                    {
                        if (_subscribedClassHashMap.TryRemove(deadSender, out var senderEvents))
                        {
                            foreach (var key in senderEvents.Keys)
                            {
                                var originalDelegate = key.action;
                                var eventType = key.eventType;

                                if (_wrapperMap.TryRemove((deadSender, originalDelegate, eventType), out var wrappedDelegate))
                                {
                                    if (_typeToEventDelegates.TryGetValue(eventType, out var repo))
                                    {
                                        ((IEventRepository)repo).Remove(wrappedDelegate);
                                    }
                                }

                                var typeKey = (deadSender, eventType);
                                if (_senderTypeIndex.TryGetValue(typeKey, out var methodSet))
                                {
                                    methodSet.TryRemove(originalDelegate, out _);
                                    if (methodSet.IsEmpty) _senderTypeIndex.TryRemove(typeKey, out _);
                                }
                            }
                        }
                    }
                    else
                    {
                        // Queue is empty, stop processing
                        break;
                    }
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Initialize()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }
        private static int _mainThreadId;

        private static bool IsMainThread()
        {
            return Thread.CurrentThread.ManagedThreadId == _mainThreadId;
        }
        #endregion
    }
}