using System;
using System.Collections.Generic;

namespace Spaxtek.Ble.Core
{
    public sealed class BleScanFilter
    {
        public BleScanFilter(string deviceName = null, Guid? serviceUuid = null, bool allowDuplicates = false)
        {
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName.Trim();
            ServiceUuid = serviceUuid;
            AllowDuplicates = allowDuplicates;
        }

        public string DeviceName { get; }
        public Guid? ServiceUuid { get; }
        public bool AllowDuplicates { get; }
    }

    public sealed class BleAdvertisement
    {
        private readonly Guid[] _serviceUuids;
        private readonly byte[] _manufacturerData;

        public BleAdvertisement(BleDeviceId deviceId, string name, int rssi, bool isConnectable, IReadOnlyList<Guid> serviceUuids = null, byte[] manufacturerData = null)
        {
            DeviceId = deviceId;
            Name = name ?? string.Empty;
            Rssi = rssi;
            IsConnectable = isConnectable;
            _serviceUuids = CopyGuids(serviceUuids);
            _manufacturerData = manufacturerData == null ? Array.Empty<byte>() : (byte[])manufacturerData.Clone();
        }

        public BleDeviceId DeviceId { get; }
        public string Name { get; }
        public int Rssi { get; }
        public bool IsConnectable { get; }
        public IReadOnlyList<Guid> ServiceUuids => _serviceUuids;
        public ReadOnlyMemory<byte> ManufacturerData => _manufacturerData;

        private static Guid[] CopyGuids(IReadOnlyList<Guid> values)
        {
            if (values == null || values.Count == 0)
                return Array.Empty<Guid>();

            Guid[] copy = new Guid[values.Count];
            for (int index = 0; index < values.Count; index++)
                copy[index] = values[index];
            return copy;
        }
    }

    public sealed class BleGattCharacteristic
    {
        public BleGattCharacteristic(BleCharacteristicId id, BleCharacteristicProperties properties)
        {
            Id = id;
            Properties = properties;
        }

        public BleCharacteristicId Id { get; }
        public BleCharacteristicProperties Properties { get; }
    }

    public sealed class BleGattService
    {
        private readonly BleGattCharacteristic[] _characteristics;

        public BleGattService(Guid uuid, IReadOnlyList<BleGattCharacteristic> characteristics)
        {
            if (uuid == Guid.Empty)
                throw new ArgumentException("A service UUID is required.", nameof(uuid));

            Uuid = uuid;
            _characteristics = CopyCharacteristics(characteristics);
        }

        public Guid Uuid { get; }
        public IReadOnlyList<BleGattCharacteristic> Characteristics => _characteristics;

        private static BleGattCharacteristic[] CopyCharacteristics(IReadOnlyList<BleGattCharacteristic> values)
        {
            if (values == null || values.Count == 0)
                return Array.Empty<BleGattCharacteristic>();

            BleGattCharacteristic[] copy = new BleGattCharacteristic[values.Count];
            for (int index = 0; index < values.Count; index++)
                copy[index] = values[index];
            return copy;
        }
    }

    public sealed class BleNotification
    {
        private readonly byte[] _data;

        public BleNotification(BleCharacteristicId characteristicId, byte[] data)
        {
            CharacteristicId = characteristicId;
            _data = data == null ? Array.Empty<byte>() : (byte[])data.Clone();
        }

        public BleCharacteristicId CharacteristicId { get; }
        public ReadOnlyMemory<byte> Data => _data;
    }

    public readonly struct BleWriteResult
    {
        public BleWriteResult(bool succeeded, BleErrorCode errorCode, string detail)
        {
            Succeeded = succeeded;
            ErrorCode = errorCode;
            Detail = detail ?? string.Empty;
        }

        public bool Succeeded { get; }
        public BleErrorCode ErrorCode { get; }
        public string Detail { get; }

        public static BleWriteResult Success(string detail = null)
        {
            return new BleWriteResult(true, BleErrorCode.None, detail);
        }

        public static BleWriteResult Failure(BleErrorCode errorCode, string detail)
        {
            return new BleWriteResult(false, errorCode, detail);
        }
    }

    public sealed class BleError
    {
        public BleError(BleErrorCode code, string detail, int nativeCode = 0)
        {
            Code = code;
            Detail = detail ?? string.Empty;
            NativeCode = nativeCode;
        }

        public BleErrorCode Code { get; }
        public string Detail { get; }
        public int NativeCode { get; }

        public override string ToString()
        {
            return NativeCode == 0 ? $"{Code}: {Detail}" : $"{Code} ({NativeCode}): {Detail}";
        }
    }
}
