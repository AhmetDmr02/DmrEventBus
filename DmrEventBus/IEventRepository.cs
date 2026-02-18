using System;
using UnityEngine;

namespace DmrEventBus
{
    public interface IEventRepository
    {
        void Remove(Delegate handler);
    }
}
