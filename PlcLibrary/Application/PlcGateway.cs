using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PlcLibrary.Controller.Interfaces;
using PlcLibrary.Controller.Models;
using PlcLibrary.DriverDomain.Interfaces;
using PlcLibrary.DriverDomain.Models;
using PlcLibrary.General.Configuration;

namespace PlcLibrary.Application
{
    internal sealed class PlcGateway(
        IDeviceScheduler scheduler,
        IDeviceAccessor accessor,
        IDeviceBrowser browser) : IPlcGateway
    {
        public Task ApplyDevicesAsync(
            IReadOnlyList<DeviceConfiguration> devices,
            CancellationToken ct = default)
            => scheduler.ApplyDevicesAsync(devices, ct);

        public Task<IReadOnlyList<DeviceHealthInfo>> GetHealthAsync(CancellationToken ct = default)
            => scheduler.GetDeviceHealthAsync(ct);

        public Task<DriverResult[]> ReadAsync(
            DeviceConfiguration device,
            TagPointConfiguration[] points,
            CancellationToken ct = default)
            => accessor.ReadAsync(device, points, ct);

        public Task<DriverResult[]> WriteAsync(
            DeviceConfiguration device,
            IReadOnlyDictionary<TagPointConfiguration, object> values,
            CancellationToken ct = default)
            => accessor.WriteAsync(device, values, ct);

        public async Task<DriverResult?> ReadTagAsync(
            DeviceConfiguration device,
            string tagId,
            CancellationToken ct = default)
        {
            var point = device.TagPoints.FirstOrDefault(p => p.TagId == tagId)
                ?? throw new ArgumentException(
                    $"Tag '{tagId}' not found in device '{device.Id}'.", nameof(tagId));

            var results = await accessor.ReadAsync(device, [point], ct).ConfigureAwait(false);
            return results.FirstOrDefault();
        }

        public Task<IReadOnlyList<TagDescriptor>> BrowseAsync(
            DeviceConfiguration device,
            string? address = null,
            CancellationToken ct = default)
            => browser.BrowseAsync(device, address, ct);
    }
}