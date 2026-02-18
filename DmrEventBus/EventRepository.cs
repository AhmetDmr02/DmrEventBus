using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace DmrEventBus
{
    public class EventRepository<T> : IEventRepository
    {
        private readonly object _lock = new object();

        private volatile Action<T>[] _activeHandlers = Array.Empty<Action<T>>();

        public void Add(Action<T> handler)
        {
            lock (_lock)
            {
                var oldArray = _activeHandlers;
                var newArray = new Action<T>[oldArray.Length + 1];

                Array.Copy(oldArray, newArray, oldArray.Length);

                newArray[oldArray.Length] = handler;

                _activeHandlers = newArray;
            }
        }

        public void Remove(Delegate handler)
        {
            Remove((Action<T>)handler);
        }

        public void Remove(Action<T> handler)
        {
            lock (_lock)
            {
                var oldArray = _activeHandlers;
                int index = Array.IndexOf(oldArray, handler);

                if (index < 0) return;

                var newArray = new Action<T>[oldArray.Length - 1];

                if (index > 0)
                    Array.Copy(oldArray, 0, newArray, 0, index);

                if (index < oldArray.Length - 1)
                    Array.Copy(oldArray, index + 1, newArray, index, oldArray.Length - index - 1);

                _activeHandlers = newArray;
            }
        }

        public void Publish(T @event)
        {
            var handlers = _activeHandlers;

            for (int i = 0; i < handlers.Length; i++)
            {
                try
                {
                    handlers[i](@event);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"EventBus Error {typeof(T).Name}: {ex}");
                }
            }
        }
    }
}
