using UnityEngine;

namespace DmrEventBus
{
    // A dummy script that dies quickly for Lifecycle Testing
    public class TemporaryListener : MonoBehaviour
    {
        private void Start()
        {
            EventBus.Subscribe<LogicEvent>(this, OnEvent);
        }

        private void OnEvent(LogicEvent e)
        {
            // Logic to verify event receipt
        }
    }
}
