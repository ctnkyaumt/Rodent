namespace Rodent.Core.Devices;

/// <summary>
/// Builds the driver for a recognised non-Logitech mouse. Logitech devices go
/// through <see cref="LogiDevice"/> and the HID++ transport instead.
///
/// Recognition is by vid/pid only — no report is sent to the device here. The
/// brand drivers are hardware-unverified in Rodent, so blindly probing every
/// SteelSeries/Glorious/ASUS HID interface on a user's machine would be reckless;
/// instead a recognised device is listed (name + brand + "experimental" note)
/// and its ported read/command code is enabled per-model only once verified.
/// </summary>
public static class DeviceFactory
{
    /// <summary>A recognised non-Logitech device, or null if the vid/pid is unknown.</summary>
    public static IDeviceDriver? Create(ushort vendorId, ushort productId, string devicePath) =>
        Vendors.Of(vendorId) switch
        {
            Brand.SteelSeries when SteelSeriesDevice.Known.ContainsKey(productId) => new SteelSeriesDevice(productId, devicePath),
            Brand.Glorious when GloriousDevice.Known.ContainsKey(productId) => new GloriousDevice(productId, devicePath),
            Brand.Asus when AsusDevice.Known.ContainsKey(productId) => new AsusDevice(productId, devicePath),
            Brand.Roccat when RoccatDevice.Known.ContainsKey(productId) => new RoccatDevice(productId, devicePath),
            Brand.MarsGaming when MarsGamingDevice.Known.ContainsKey(productId) => new MarsGamingDevice(productId, devicePath),
            _ => null,
        };

    /// <summary>True if this vendor has a driver (real or stub) worth recognising.</summary>
    public static bool IsKnownBrand(ushort vendorId) =>
        Vendors.Of(vendorId) is not (Brand.Logitech or Brand.Unknown);
}
