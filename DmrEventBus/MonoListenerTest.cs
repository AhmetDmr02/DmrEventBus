using UnityEngine;

namespace DmrEventBus
{
    public class MonoListenerTest : MonoBehaviour
    {
        void Start()
        {
            EventBus.Subscribe<StressEvent>(this, OnEvent);
        }

        void OnEvent(StressEvent e) { 
        Debug.Log($"MonoListenerTest received value: {e.Value}");
        }
    }
}
