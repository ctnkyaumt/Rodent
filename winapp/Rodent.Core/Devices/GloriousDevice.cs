namespace Rodent.Core.Devices;

/// <summary>
/// Glorious and other SinoWealth-based mice (vendor 0x258A). STUB — the
/// SinoWealth HID protocol (DPI, RGB, button remap) is not ported yet; port from
/// libratbag src/driver-sinowealth.c.
/// </summary>
public sealed class GloriousDevice : StubDeviceDriver
{
    public GloriousDevice(ushort productId, string devicePath)
        : base(Vendors.Glorious, productId, devicePath, Known.GetValueOrDefault(productId, "SinoWealth mouse")) { }

    public override Brand Brand => Brand.Glorious;
    public override string ReferenceDriver => "libratbag src/driver-sinowealth.c";

    public static readonly Dictionary<ushort, string> Known = new()
    {
        [0x0012] = "SN-Tech T3",           // generic SinoWealth reference board
        [0x0033] = "Glorious Model D",
        [0x0036] = "Glorious Model O",
    };
}
