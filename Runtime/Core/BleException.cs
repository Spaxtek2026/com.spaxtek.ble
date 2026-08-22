using System;

namespace Spaxtek.Ble.Core
{
    public sealed class BleException : Exception
    {
        public BleException(BleErrorCode errorCode, string message, int nativeCode = 0, Exception innerException = null)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
            NativeCode = nativeCode;
        }

        public BleErrorCode ErrorCode { get; }
        public int NativeCode { get; }
    }
}
