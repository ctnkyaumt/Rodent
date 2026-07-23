namespace Rodent.Core.Devices;

/// <summary>Vendors Rodent knows about (whether or not a driver is implemented).</summary>
public enum Brand
{
    Logitech,
    Asus,
    SteelSeries,
    Roccat,
    Glorious,
    MarsGaming,
    Unknown,
}

/// <summary>
/// Common surface every device driver exposes, regardless of brand/protocol.
/// <see cref="LogiDevice"/> is the only full implementation (HID++ 2.0); the other
/// brands are <see cref="StubDeviceDriver"/>s — recognised and named, but their
/// protocols aren't ported yet (Initialize returns false). This interface is the
/// seam future brand drivers plug into.
/// </summary>
public interface IDeviceDriver : IDisposable
{
    Brand Brand { get; }
    ushort VendorId { get; }
    ushort ProductId { get; }
    string DevicePath { get; }
    string Name { get; }
    string Kind { get; }
    DeviceSupport Support { get; }

    /// <summary>Probe the device and load its state. False = not usable (unknown
    /// device, or — for the stub brands — protocol not implemented yet).</summary>
    bool Initialize();
}

/// <summary>USB vendor ids and brand identification.</summary>
public static class Vendors
{
    public const ushort Logitech = 0x046D;
    public const ushort Asus = 0x0B05;
    public const ushort SteelSeries = 0x1038;
    public const ushort Roccat = 0x1E7D;
    public const ushort Glorious = 0x258A;   // SinoWealth (Glorious, and other rebrands)
    public const ushort MarsGaming = 0x04D9;

    public static Brand Of(ushort vendorId) => vendorId switch
    {
        Logitech => Brand.Logitech,
        Asus => Brand.Asus,
        SteelSeries => Brand.SteelSeries,
        Roccat => Brand.Roccat,
        Glorious => Brand.Glorious,
        MarsGaming => Brand.MarsGaming,
        _ => Brand.Unknown,
    };
}
