namespace Rodent.Core.Devices;

/// <summary>
/// Maps a recognised non-Logitech mouse to its (stub) brand driver. Logitech
/// devices go through <see cref="LogiDevice"/> and the HID++ transport instead;
/// this is only for the other brands, whose protocols aren't implemented yet.
/// When a brand's real driver lands, replace its stub construction here.
/// </summary>
public static class DeviceFactory
{
    /// <summary>A recognised non-Logitech device, or null if the vid/pid is unknown.</summary>
    public static StubDeviceDriver? CreateStub(ushort vendorId, ushort productId, string devicePath) =>
        Vendors.Of(vendorId) switch
        {
            Brand.Asus when AsusDevice.Known.ContainsKey(productId) => new AsusDevice(productId, devicePath),
            Brand.SteelSeries when SteelSeriesDevice.Known.ContainsKey(productId) => new SteelSeriesDevice(productId, devicePath),
            Brand.Roccat when RoccatDevice.Known.ContainsKey(productId) => new RoccatDevice(productId, devicePath),
            Brand.Glorious when GloriousDevice.Known.ContainsKey(productId) => new GloriousDevice(productId, devicePath),
            Brand.MarsGaming when MarsGamingDevice.Known.ContainsKey(productId) => new MarsGamingDevice(productId, devicePath),
            _ => null,
        };
}
