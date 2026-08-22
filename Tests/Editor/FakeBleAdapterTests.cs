using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Spaxtek.Ble.Core;
using Spaxtek.Ble.Testing;

namespace Spaxtek.Ble.Tests
{
    public sealed class FakeBleAdapterTests
    {
        private static readonly Guid ServiceUuid = Guid.Parse("7a1e0001-8e88-4c19-9e6a-31b9d0a4f001");
        private static readonly Guid NotifyUuid = Guid.Parse("7a1e0002-8e88-4c19-9e6a-31b9d0a4f001");
        private static readonly Guid WriteUuid = Guid.Parse("7a1e0003-8e88-4c19-9e6a-31b9d0a4f001");

        [Test]
        public async Task ScanFiltersByNameAndService()
        {
            FakeBleAdapter adapter = CreateAdapter();
            List<BleAdvertisement> results = new List<BleAdvertisement>();
            adapter.OnAdvertisementReceived += results.Add;

            await adapter.InitializeAsync(CancellationToken.None);
            await adapter.StartScanAsync(new BleScanFilter("TEST-DEVICE", ServiceUuid), CancellationToken.None);

            Assert.That(results.Count, Is.EqualTo(1));
            Assert.That(adapter.State, Is.EqualTo(BleAdapterState.Scanning));
            adapter.Dispose();
        }

        [Test]
        public async Task ConnectionDiscoversSubscribesNotifiesAndWrites()
        {
            FakeBleAdapter adapter = CreateAdapter();
            await adapter.InitializeAsync(CancellationToken.None);
            BleDeviceId deviceId = new BleDeviceId(BlePlatform.Simulated, "test-device");
            FakeBleConnection connection = (FakeBleConnection)await adapter.ConnectAsync(deviceId, CancellationToken.None);

            IReadOnlyList<BleGattService> services = await connection.DiscoverServicesAsync(BleCacheMode.Uncached, CancellationToken.None);
            BleCharacteristicId notifyId = new BleCharacteristicId(ServiceUuid, NotifyUuid);
            BleCharacteristicId writeId = new BleCharacteristicId(ServiceUuid, WriteUuid);
            BleNotification notification = null;
            connection.OnNotificationReceived += value => notification = value;

            await connection.SubscribeAsync(notifyId, CancellationToken.None);
            connection.SetValue(writeId, new byte[] { 9, 8, 7 });
            ReadOnlyMemory<byte> readValue = await connection.ReadAsync(writeId, CancellationToken.None);
            bool emitted = connection.EmitNotification(notifyId, new byte[] { 1, 2, 3 });
            BleWriteResult writeResult = await connection.WriteAsync(writeId, new byte[] { 4, 5, 6 }, BleWriteMode.WithoutResponse, CancellationToken.None);

            Assert.That(services.Count, Is.EqualTo(1));
            Assert.That(emitted, Is.True);
            Assert.That(notification, Is.Not.Null);
            Assert.That(notification.Data.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(readValue.ToArray(), Is.EqualTo(new byte[] { 9, 8, 7 }));
            Assert.That(writeResult.Succeeded, Is.True);
            Assert.That(connection.Writes.Count, Is.EqualTo(1));
            adapter.Dispose();
        }

        private static FakeBleAdapter CreateAdapter()
        {
            BleCharacteristicId notifyId = new BleCharacteristicId(ServiceUuid, NotifyUuid);
            BleCharacteristicId writeId = new BleCharacteristicId(ServiceUuid, WriteUuid);
            BleGattCharacteristic notifyCharacteristic = new BleGattCharacteristic(notifyId, BleCharacteristicProperties.Notify);
            BleCharacteristicProperties writeProperties = BleCharacteristicProperties.Read | BleCharacteristicProperties.WriteWithoutResponse;
            BleGattCharacteristic writeCharacteristic = new BleGattCharacteristic(writeId, writeProperties);
            BleGattService service = new BleGattService(ServiceUuid, new[] { notifyCharacteristic, writeCharacteristic });
            BleDeviceId deviceId = new BleDeviceId(BlePlatform.Simulated, "test-device");
            BleAdvertisement advertisement = new BleAdvertisement(deviceId, "TEST-DEVICE", -42, true, new[] { ServiceUuid });
            FakeBleAdapter adapter = new FakeBleAdapter();
            adapter.RegisterPeripheral(advertisement, new[] { service });
            return adapter;
        }
    }
}
