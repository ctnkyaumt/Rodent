namespace Rodent.Core.Devices;

/// <summary>
/// SteelSeries gaming mice (vendor 0x1038). STUB — the SteelSeries HID protocol
/// (DPI, report rate, RGB, button remap) is not ported yet; port from libratbag
/// src/driver-steelseries.c.
/// </summary>
public sealed class SteelSeriesDevice : StubDeviceDriver
{
    public SteelSeriesDevice(ushort productId, string devicePath)
        : base(Vendors.SteelSeries, productId, devicePath, Known.GetValueOrDefault(productId, "SteelSeries mouse")) { }

    public override Brand Brand => Brand.SteelSeries;
    public override string ReferenceDriver => "libratbag src/driver-steelseries.c";

    public static readonly Dictionary<ushort, string> Known = new()
    {
        [0x1369] = "Sensei RAW",
        [0x1378] = "Kinzu v2",
        [0x1384] = "Rival",
        [0x1388] = "Kinzu v3",
        [0x1720] = "Rival 310",
        [0x1722] = "Sensei 310",
        [0x1724] = "Rival 600",
        [0x1832] = "Sensei Ten",
    };
}
