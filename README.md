# DmrEventBus

Plain C# events in Unity have a well known problem, if a MonoBehaviour subscribes to an event and gets destroyed, the delegate still holds a reference to it. Next time that event fires, you get a `MissingReferenceException`. The usual fix is to always unsubscribe in `OnDestroy` which works until someone forgets, which happens constantly.

DmrEventBus handles this automatically. Subscribers are checked for liveness at invocation time, dead ones are cleaned up silently, and you don't need to pair every `Subscribe` with an `OnDestroy` unsubscribe.

It also fully supports publishing from background threads.

> Prioritizes publish speed and correctness over zero-allocation subscriptions. Subscribe/Unsubscribe allocate by design see the Architecture section for why.

---

## Features

- **Automatic zombie cleanup**: Destroyed MonoBehaviours are detected at invocation time and queued for removal. No `MissingReferenceException`, no manual `OnDestroy` unsubscription required.
- **Thread-safe publishing**: Background threads can publish freely.
- **`IChildEventListener`**: Plain C# classes can tie their lifetime to a parent `GameObject` and get cleaned up automatically when it dies.
- **Duplicate guard**: Subscribing the same sender + action twice logs a warning and skips instead of silently doubling up.
---

## Installation

Copy the `DmrEventBus` folder into your Unity project's `Assets` directory. No package manager setup required.

---

## Quick Start

### 1. Define an event

```csharp
public struct PlayerDiedEvent
{
    public int PlayerId;
    public Vector3 Position;
}
```

### 2. Subscribe

```csharp
public class GameManager : MonoBehaviour
{
    void OnEnable()
    {
        EventBus.Subscribe<PlayerDiedEvent>(this, OnPlayerDied);
    }

    void OnDisable()
    {
        // Explicit unsubscribe is recommended, though dead objects are cleaned up automatically.
        EventBus.Unsubscribe<PlayerDiedEvent>(this, OnPlayerDied);
    }

    void OnPlayerDied(PlayerDiedEvent e)
    {
        Debug.Log($"Player {e.PlayerId} died at {e.Position}");
    }
}
```

### 3. Publish

```csharp
// Safe to call from any thread (Of course not the unity API ones.)
EventBus.Publish(new PlayerDiedEvent { PlayerId = 1, Position = transform.position });
```

---

## API Reference

| Method | Description |
|--------|-------------|
| `EventBus.Subscribe<T>(sender, action)` | Register a listener. `sender` must be a `MonoBehaviour`, `IChildEventListener`, `ScriptableObject`, or `IDisposable`. |
| `EventBus.Unsubscribe<T>(sender, action)` | Remove a specific listener. |
| `EventBus.UnsubscribeAll<T>(sender)` | Remove all listeners of type `T` registered by `sender`. |
| `EventBus.Publish<T>(event)` | Dispatch an event to all registered listeners. Thread-safe. |

---

## Sender Types

### MonoBehaviour

The primary use case. Zombie check runs automatically on the main thread before each invocation.

```csharp
EventBus.Subscribe<MyEvent>(this, OnMyEvent);
```

### ScriptableObject

ScriptableObjects persist across scene loads and lack a reliable destroyed state. **Manual unsubscription is required.**

```csharp
// In your ScriptableObject
void OnDisable()
{
    EventBus.Unsubscribe<MyEvent>(this, OnMyEvent);
}
```

### Plain C# class with `IChildEventListener`

Bind a plain class to a parent `GameObject`. The bus will auto unsubscribe it when the parent is destroyed.

```csharp
public class InventorySystem : IChildEventListener
{
    private readonly GameObject _owner;

    public InventorySystem(GameObject owner) => _owner = owner;

    public UnityEngine.Object ReturnParentGameObject() => _owner;
}
```

### Plain C# class with `IDisposable`

For classes not tied to a GameObject. Unsubscription must be called manually in `Dispose()`.

```csharp
public class NetworkHandler : IDisposable
{
    public NetworkHandler()
    {
        EventBus.Subscribe<ServerEvent>(this, OnServerEvent);
    }

    public void Dispose()
    {
        EventBus.UnsubscribeAll<ServerEvent>(this);
    }
}
```

---

## Architecture & Design

### No `OnDestroy` Unsubscription Required

The whole point of this system is that MonoBehaviours don't need to manually unsubscribe.

Each MonoBehaviour subscriber gets wrapped in a lambda that runs `IsUnityObjectDead()` before invoking the actual handler. Unity overrides `==` on `UnityEngine.Object` comparing a destroyed object to null returns true even though the managed C# shell is still sitting in memory. That check `if (obj is UnityEngine.Object u) return u == null;` is the only reliable way to detect the zombie state, and it runs for free on every invocation. If the object is dead, the call is skipped and the sender gets queued for cleanup.

You can still call `Unsubscribe` in `OnDisable` if you want. It'll work correctly. It's just not required.

### Copy-On-Write

The subscriber list for each event type is a `volatile` array reference. Publishing captures that reference into a local variable and iterates it no locks, no allocations. **BUT** Subscribe and Unsubscribe take a write lock, allocate a fresh array, copy everything over, then swap the reference which creates a Garbage.

The reason this works safely across threads is that once `Publish()` has captured its local snapshot, nothing can touch that array. A concurrent unsubscribe on another thread will swap in a new array, but the publish loop doesn't care it's already holding the old one.

This is deliberately optimized for the actual usage pattern. Events get published many times per frame, subscriptions change rarely and only at lifecycle boundaries. The write path being slow and allocating is fine. The read path being fast is the requirement.

### Why Not `WeakReference`?

WeakReference is the standard C# advice for event buses, but it fails in Unity.
When Destroy() kills the native C++ object, the C# wrapper stays alive. WeakReference sees this wrapper and thinks the object is Alive, which prevents the system from working reliably. 
My tests also showed that POCO classes owned by MonoBehaviours are often "rooted" and refuse to let go even when using WeakReference. 
To solve this, I created the IChildEventListener interface to bind their lifetime to a parent's native state instead of relying on the GC.

### Why Not `ArrayPool`?

An earlier version used `ArrayPool` to avoid the allocations on subscribe/unsubscribe. It was removed because it introduced a race condition that can't be fixed without making the design significantly more complex.

The publish path is capture array reference → iterate. If another thread unsubscribes between those two steps, under a pooling design that array gets returned to the pool immediately. The pool can then hand it to anything else in the application. The publish loop is now walking an array that's being written by unrelated code.

You can't fix this by locking around the publish loop that's the whole point of COW, keeping the read path lock free. The correct fix is reference counting: only return the array to the pool once every active reader is done with it. That's a non-trivial amount of complexity to add to what is supposed to be a cold path optimization.

A live reference in a local variable means the array won't be collected while it's being iterated, no matter what happens on other threads. The cost is that subscribe and unsubscribe allocate, which is acceptable because they're not called per frame.

### Zombie Cleanup

When a dead sender is detected during publish:

1. The handler call is skipped.
2. The sender is pushed onto a `ConcurrentQueue`.
3. On the next `Publish()` call, up to 50 queued senders are dequeued and removed from all internal maps under the write lock.

The batch cap is there for cases like spawning and immediately destroying a large number of objects without it, the first publish after a mass destruction event would block while cleaning up potentially hundreds of entries at once.

### Threading

| Operation | Thread-safe? | Notes |
|-----------|-------------|-------|
| `Publish()` | ✅ | Lock free on the read path |
| `Subscribe()` | ✅ | Internally locked |
| `Unsubscribe()` | ✅ | Internally locked |
| Handler execution | ⚠️ | Runs on whichever thread called `Publish()` |

If you publish from a background thread and your handler touches the Unity API, you need to dispatch to the main thread yourself. The bus doesn't do that for you.

---

## Limitations

- **Subscribe/Unsubscribe allocate.** Intentional see the ArrayPool section. Don't subscribe and unsubscribe short lived objects (bullets, particles) every frame use pooling system for it. This system is great and optimal for long lived subscribers like managers, UI, and systems.
- **No main thread marshalling.** If you publish from a background thread, your handler runs on that thread. Touching `transform` or any Unity API from there will crash. Marshal yourself.
- **ScriptableObjects need manual unsubscription.** There's no reliable destroyed state to check against, so they don't get automatic cleanup. Always unsubscribe in `OnDisable`.
- **Same for plain C# `IDisposable` classes.** The GC won't consistently collect them while a delegate holds a reference. Unsubscribe in `Dispose()`.

---
