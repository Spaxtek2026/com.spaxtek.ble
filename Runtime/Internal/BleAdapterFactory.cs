using Spaxtek.Ble.Core;
using Spaxtek.Ble.Platforms;

#if UNITY_ANDROID && !UNITY_EDITOR
using Spaxtek.Ble.Platforms.Android;
#endif

#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_ANDROID)
using Spaxtek.Ble.Platforms.Windows;
#endif

namespace Spaxtek.Ble.Internal
{
    internal static class BleAdapterFactory
    {
        public static IBleAdapter Create()
        {
#if UNITY_EDITOR_WIN
            return new WindowsBleAdapter();
#elif UNITY_ANDROID && !UNITY_EDITOR
            return new AndroidBleAdapter();
#elif UNITY_STANDALONE_WIN
            return new WindowsBleAdapter();
#else
            return new UnsupportedBleAdapter();
#endif
        }
    }
}
