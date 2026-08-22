#if UNITY_EDITOR_WIN || (UNITY_STANDALONE_WIN && !UNITY_ANDROID)
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Spaxtek.Ble.Platforms.Windows
{
    internal static class WindowsBleNative
    {
        private const string LibraryName = "SpaxtekBleWindows";

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern IntPtr spax_ble_create();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern void spax_ble_destroy(IntPtr handle);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int spax_ble_initialize(IntPtr handle);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int spax_ble_start_scan(IntPtr handle, string deviceName, string serviceUuid, int allowDuplicates);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int spax_ble_stop_scan(IntPtr handle);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int spax_ble_connect(IntPtr handle, string deviceId);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int spax_ble_disconnect(IntPtr handle);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        public static extern int spax_ble_discover_services(IntPtr handle, long operationId, int uncached);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int spax_ble_set_subscription(IntPtr handle, long operationId, string serviceUuid, string characteristicUuid, int enabled);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int spax_ble_read(IntPtr handle, long operationId, string serviceUuid, string characteristicUuid);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int spax_ble_write(IntPtr handle, long operationId, string serviceUuid, string characteristicUuid, byte[] data, int dataLength, int writeMode);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int spax_ble_poll_event(IntPtr handle, ref NativeEventHeader header, StringBuilder textA, int textACapacity,
            StringBuilder textB, int textBCapacity, StringBuilder textC, int textCCapacity, byte[] data, int dataCapacity);

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeEventHeader
        {
            public int EventType;
            public int Success;
            public long OperationId;
            public int ErrorCode;
            public int NativeCode;
            public int ValueA;
            public int ValueB;
            public int DataLength;
        }
    }
}
#endif
