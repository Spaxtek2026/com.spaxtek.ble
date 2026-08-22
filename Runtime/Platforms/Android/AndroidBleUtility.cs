#if UNITY_ANDROID && !UNITY_EDITOR
using System;

namespace Spaxtek.Ble.Platforms.Android
{
    internal static class AndroidBleUtility
    {
        public static sbyte[] ToSignedBytes(byte[] data)
        {
            if (data == null)
                return Array.Empty<sbyte>();

            sbyte[] result = new sbyte[data.Length];
            for (int index = 0; index < data.Length; index++)
                result[index] = unchecked((sbyte)data[index]);
            return result;
        }

        public static byte[] ToUnsignedBytes(sbyte[] data)
        {
            if (data == null)
                return Array.Empty<byte>();

            byte[] result = new byte[data.Length];
            for (int index = 0; index < data.Length; index++)
                result[index] = unchecked((byte)data[index]);
            return result;
        }
    }
}
#endif
