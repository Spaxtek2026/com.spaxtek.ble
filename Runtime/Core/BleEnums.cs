using System;

namespace Spaxtek.Ble.Core
{
    public enum BlePlatform
    {
        Unknown = 0,
        Android = 1,
        Windows = 2,
        Simulated = 3
    }

    public enum BleAdapterState
    {
        Uninitialized = 0,
        Initializing = 1,
        Ready = 2,
        Scanning = 3,
        Unavailable = 4,
        Faulted = 5,
        Disposed = 6
    }

    public enum BleConnectionState
    {
        Connecting = 0,
        Connected = 1,
        Disconnecting = 2,
        Disconnected = 3,
        Faulted = 4,
        Disposed = 5
    }

    public enum BleWriteMode
    {
        WithResponse = 0,
        WithoutResponse = 1
    }

    public enum BleCacheMode
    {
        Cached = 0,
        Uncached = 1
    }

    public enum BleErrorCode
    {
        None = 0,
        Unsupported = 1,
        PermissionDenied = 2,
        BluetoothDisabled = 3,
        DeviceNotFound = 4,
        ConnectionTimeout = 5,
        DeviceUnreachable = 6,
        ServiceNotFound = 7,
        CharacteristicNotFound = 8,
        SubscriptionFailed = 9,
        WriteRejected = 10,
        OperationBusy = 11,
        Cancelled = 12,
        InvalidState = 13,
        InvalidArgument = 14,
        PlatformError = 15,
        Disposed = 16,
        Timeout = 17,
        ReadRejected = 18
    }

    [Flags]
    public enum BleCharacteristicProperties
    {
        None = 0,
        Read = 1 << 0,
        Write = 1 << 1,
        WriteWithoutResponse = 1 << 2,
        Notify = 1 << 3,
        Indicate = 1 << 4
    }
}
