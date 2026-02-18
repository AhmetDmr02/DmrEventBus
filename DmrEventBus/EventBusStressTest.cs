using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Profiling;

namespace DmrEventBus
{
    public struct StressEvent
    {
        public int Value;
        public int ThreadId;
    }

    public struct LogicEvent
    {
        public string Message;
    }
    public class EventBusStressTest : MonoBehaviour
    {
        [Header("Settings")]
        [SerializeField] private bool _enableConsoleSpam = false;

        [Header("Performance Loop (F3)")]
        [SerializeField] private int _eventsPerFrame = 1000;
        [SerializeField] private int _churnsPerFrame = 100; 

        [Header("Chaos Test (F1)")]
        [SerializeField] private int _threadsToSpawn = 10;
        [SerializeField] private int _eventsPerThread = 5000;

        [Tooltip("If true, the update loop test starts immediately on Play.")]
        [SerializeField] private bool _runUpdateLoopOnStart = false;

        [Tooltip("If true, F1 will also spawn threads that randomly Sub/Unsub to break the list.")]
        [SerializeField] private bool _enableChurning = false;

        [Header("References")]
        [SerializeField] private TestScriptable _testScriptable;

        private bool _isUpdateLoopRunning = false;
        private bool _isChaosTestRunning = false;

        private ThreadSafeListener _chaosListener;

        private ChurnListener _mainThreadChurnDummy;
        private Action<StressEvent> _cachedChurnAction;

        private int _receivedCount = 0;
        private int _sentCount = 0;

        private void Start()
        {
            _isUpdateLoopRunning = _runUpdateLoopOnStart;

            _chaosListener = new ThreadSafeListener(this);
            EventBus.Subscribe<StressEvent>(_chaosListener, _chaosListener.OnStressEventReceived);

            _mainThreadChurnDummy = new ChurnListener();
            _cachedChurnAction = _mainThreadChurnDummy.OnEvent;

            if (_testScriptable != null) _testScriptable.Listen(this);

            Debug.Log("<b>[EventBusTest]</b> Controls: [F1] Chaos, [F2] Lifecycle, [F3] Update Loop, [F4] Churn Mode");
        }

        private void OnDestroy()
        {
            if (_chaosListener != null) EventBus.UnsubscribeAll<StressEvent>(_chaosListener);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1)) RunChaosTest();
            if (Input.GetKeyDown(KeyCode.F2)) RunLifecycleTest();
            if (Input.GetKeyDown(KeyCode.F3)) ToggleUpdateLoop();
            if (Input.GetKeyDown(KeyCode.F4)) ToggleChurn();

            if (_isUpdateLoopRunning)
            {
                Profiler.BeginSample(">> EventBus Publish (Hot Path)");
                for (int i = 0; i < _eventsPerFrame; i++)
                {
                    EventBus.Publish(new StressEvent { Value = i, ThreadId = -1 });
                }
                Profiler.EndSample();

                Profiler.BeginSample(">> EventBus Churn (Alloc Path)");
                for (int i = 0; i < _churnsPerFrame; i++)
                {
                    EventBus.Subscribe(_mainThreadChurnDummy, _cachedChurnAction);
                    EventBus.Unsubscribe(_mainThreadChurnDummy, _cachedChurnAction);
                }
                Profiler.EndSample();
            }
        }

        private void ToggleUpdateLoop()
        {
            _isUpdateLoopRunning = !_isUpdateLoopRunning;
            Debug.Log($"<b>[Update Loop]</b> is now {(_isUpdateLoopRunning ? "<color=green>ON</color>" : "<color=red>OFF</color>")}.");
        }

        private void ToggleChurn()
        {
            _enableChurning = !_enableChurning;
            Debug.Log($"<b>[Churn Mode]</b> is now {(_enableChurning ? "<color=red>ON</color>" : "OFF")}.");
        }

        private async void RunChaosTest()
        {
            if (_isChaosTestRunning)
            {
                Debug.LogWarning("Test already running! Wait for it to finish.");
                return;
            }
            _isChaosTestRunning = true;

            Debug.Log($"<color=yellow>Starting Chaos Test (Churning: {_enableChurning})...</color>");

            _receivedCount = 0;
            _sentCount = 0;

            try
            {
                var publisherTasks = new Task[_threadsToSpawn];
                for (int i = 0; i < _threadsToSpawn; i++)
                {
                    int threadIndex = i;
                    publisherTasks[i] = Task.Run(() =>
                    {
                        for (int j = 0; j < _eventsPerThread; j++)
                        {
                            EventBus.Publish(new StressEvent { Value = j, ThreadId = threadIndex });
                            Interlocked.Increment(ref _sentCount);
                        }
                    });
                }

                Task[] churnTasks = null;
                if (_enableChurning)
                {
                    churnTasks = new Task[_threadsToSpawn];
                    for (int i = 0; i < _threadsToSpawn; i++)
                    {
                        churnTasks[i] = Task.Run(() =>
                        {
                            var dummy = new ChurnListener();
                            for (int j = 0; j < 2000; j++)
                            {
                                EventBus.Subscribe<StressEvent>(dummy, dummy.OnEvent);
                                EventBus.UnsubscribeAll<StressEvent>(dummy);
                            }
                        });
                    }
                }

                await Task.WhenAll(publisherTasks);
                if (churnTasks != null) await Task.WhenAll(churnTasks);

                Debug.Log($"<color=green>Chaos Test Finished!</color>");
                Debug.Log($"Sent: {_sentCount} | Received: {_receivedCount}");

                if (_sentCount == _receivedCount)
                    Debug.Log("<color=cyan>SUCCESS: No events lost.</color>");
                else
                    Debug.LogError($"FAILURE: Count mismatch. Diff: {_receivedCount - _sentCount}.");
            }
            finally
            {
                _isChaosTestRunning = false;
            }
        }

        private void RunLifecycleTest()
        {
            Debug.Log("<color=yellow>Starting Lifecycle Test...</color>");
            int spawnCount = 100;

            for (int i = 0; i < spawnCount; i++)
            {
                GameObject temp = new GameObject($"Temp_Listener_{i}");
                temp.AddComponent<TemporaryListener>();
                Destroy(temp);
            }

            for (int i = 0; i < spawnCount * 2; i++)
            {
                EventBus.Publish(new LogicEvent { Message = "Wake up cleanup" });
            }

            Debug.Log("<color=green>Lifecycle Test Done.</color>");
        }

        private class ThreadSafeListener : IDisposable
        {
            private readonly EventBusStressTest _owner;

            public ThreadSafeListener(EventBusStressTest owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                EventBus.UnsubscribeAll<StressEvent>(this);
            }

            public void OnStressEventReceived(StressEvent e)
            {
                if (e.ThreadId == -1) return;

                Interlocked.Increment(ref _owner._receivedCount);

                if (_owner._enableConsoleSpam)
                {
                    Debug.Log($"[Thread {e.ThreadId}] Val: {e.Value}");
                }
            }
        }

        private class ChurnListener : IDisposable
        {
            public void Dispose()
            {

            }

            public void OnEvent(StressEvent e) { }
        }
    }
}
