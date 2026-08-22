using System;
using NUnit.Framework;
using Spaxtek.Ble.Core;

namespace Spaxtek.Ble.Tests
{
    public sealed class BleIdentifiersTests
    {
        [Test]
        public void DeviceIdUsesPlatformAndValueForEquality()
        {
            BleDeviceId first = new BleDeviceId(BlePlatform.Android, "device-a");
            BleDeviceId same = new BleDeviceId(BlePlatform.Android, "device-a");
            BleDeviceId differentPlatform = new BleDeviceId(BlePlatform.Windows, "device-a");

            Assert.That(first, Is.EqualTo(same));
            Assert.That(first, Is.Not.EqualTo(differentPlatform));
        }

        [Test]
        public void CharacteristicIdRequiresServiceAndCharacteristicUuid()
        {
            Assert.Throws<ArgumentException>(() => new BleCharacteristicId(Guid.Empty, Guid.NewGuid()));
            Assert.Throws<ArgumentException>(() => new BleCharacteristicId(Guid.NewGuid(), Guid.Empty));
        }
    }
}
