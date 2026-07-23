namespace Rodent.Core.Devices;

/// <summary>
/// Roccat gaming mice (vendor 0x1E7D). STUB — the Roccat HID protocol (profiles,
/// DPI, button remap, RGB) is not ported yet; port from libratbag
/// src/driver-roccat.c and src/driver-roccat-kone-pure.c.
/// </summary>
public sealed class RoccatDevice : StubDeviceDriver
{
    public RoccatDevice(ushort productId, string devicePath)
        : base(Vendors.Roccat, productId, devicePath, Known.GetValueOrDefault(productId, "Roccat mouse")) { }

    public override Brand Brand => Brand.Roccat;
    public override string ReferenceDriver => "libratbag src/driver-roccat.c";

    public static readonly Dictionary<ushort, string> Known = new()
    {
        [0x2DBE] = "Kone Pure",
        [0x2DC2] = "Kone Pure",
        [0x2E22] = "Kone XTD",
    };
}
