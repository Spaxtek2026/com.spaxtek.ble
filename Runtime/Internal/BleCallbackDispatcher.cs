using System;
using System.Collections.Concurrent;

namespace Spaxtek.Ble.Internal
{
    internal sealed class BleCallbackDispatcher
    {
        private readonly ConcurrentQueue<Action> _callbacks = new ConcurrentQueue<Action>();

        public void Enqueue(Action callback)
        {
            if (callback == null)
                return;

            _callbacks.Enqueue(callback);
        }

        public int Drain(int maximumCallbackCount)
        {
            if (maximumCallbackCount <= 0)
                return 0;

            int callbackCount = 0;
            while (callbackCount < maximumCallbackCount && _callbacks.TryDequeue(out Action callback))
            {
                callbackCount++;
                callback.Invoke();
            }

            return callbackCount;
        }

        public void Clear()
        {
            while (_callbacks.TryDequeue(out Action _))
            {
            }
        }
    }
}
