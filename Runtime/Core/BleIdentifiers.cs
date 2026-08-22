using System;

namespace Spaxtek.Ble.Core
{
    public readonly struct BleDeviceId : IEquatable<BleDeviceId>
    {
        public BleDeviceId(BlePlatform platform, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("A platform device identifier is required.", nameof(value));

            Platform = platform;
            Value = value;
        }

        public BlePlatform Platform { get; }
        public string Value { get; }

        public bool Equals(BleDeviceId other)
        {
            return Platform == other.Platform && string.Equals(Value, other.Value, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is BleDeviceId other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine((int)Platform, Value);
        }

        public override string ToString()
        {
            return $"{Platform}:{Value}";
        }

        public static bool operator ==(BleDeviceId left, BleDeviceId right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(BleDeviceId left, BleDeviceId right)
        {
            return !left.Equals(right);
        }
    }

    public readonly struct BleCharacteristicId : IEquatable<BleCharacteristicId>
    {
        public BleCharacteristicId(Guid serviceUuid, Guid characteristicUuid)
        {
            if (serviceUuid == Guid.Empty)
                throw new ArgumentException("A service UUID is required.", nameof(serviceUuid));
            if (characteristicUuid == Guid.Empty)
                throw new ArgumentException("A characteristic UUID is required.", nameof(characteristicUuid));

            ServiceUuid = serviceUuid;
            CharacteristicUuid = characteristicUuid;
        }

        public Guid ServiceUuid { get; }
        public Guid CharacteristicUuid { get; }

        public bool Equals(BleCharacteristicId other)
        {
            return ServiceUuid.Equals(other.ServiceUuid) && CharacteristicUuid.Equals(other.CharacteristicUuid);
        }

        public override bool Equals(object obj)
        {
            return obj is BleCharacteristicId other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(ServiceUuid, CharacteristicUuid);
        }

        public override string ToString()
        {
            return $"{ServiceUuid:D}/{CharacteristicUuid:D}";
        }

        public static bool operator ==(BleCharacteristicId left, BleCharacteristicId right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(BleCharacteristicId left, BleCharacteristicId right)
        {
            return !left.Equals(right);
        }
    }
}
