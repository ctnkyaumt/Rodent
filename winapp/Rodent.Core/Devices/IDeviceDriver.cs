using Rodent.Core.Model;

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
/// Common surface every device driver exposes, regardless of brand/protocol:
/// identity, readable info, and the generic settings list.
///
/// Anything beyond that is a capability interface — <see cref="IButtonDevice"/>,
/// <see cref="IMacroDevice"/>, <see cref="ILightingDevice"/>,
/// <see cref="IDpiDevice"/>, <see cref="IHidppDevice"/> — which a driver
/// implements once it can actually do that job. Callers ask for the capability,
/// never for a brand, so a new brand driver lights up the existing UI and tools
/// the moment it implements one. <see cref="LogiDevice"/> (HID++ 2.0) implements
/// them all today; the other brand drivers implement what their ported protocol
/// supports so far.
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

    /// <summary>Generic settings the UI renders (DPI, report rate, …). Empty for
    /// stub brands until their protocol is ported.</summary>
    IReadOnlyList<Setting> Settings { get; }

    /// <summary>Read-only info chips (firmware, …).</summary>
    IReadOnlyList<InfoItem> Info { get; }

    /// <summary>Main firmware version, or null. Drivers that publish it as an
    /// info chip get this for free.</summary>
    string? Firmware => Info.FirstOrDefault(i => i.Label == "Firmware")?.Value;

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
