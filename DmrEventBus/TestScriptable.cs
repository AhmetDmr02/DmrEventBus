using UnityEngine;

namespace DmrEventBus
{
    [CreateAssetMenu(fileName = "TestScriptable", menuName = "DmrEventBus/TestScriptable")]
    public class TestScriptable : ScriptableObject, IChildEventListener
    {
        private Object _parentCache;

        public void Listen(Object parent)
        {
            _parentCache = parent;

            EventBus.Subscribe<StressEvent>(this, (e) =>
            {
                //Debug.Log($"TestScriptable received value: {e.Value}"); 
            });
        }

        public Object ReturnParentGameObject()
        {
            return _parentCache;
        }
    }
}
