namespace Rodent.Core.Devices;

/// <summary>
/// ASUS ROG / TUF gaming mice (vendor 0x0B05). STUB — the ASUS vendor HID
/// protocol (button remap, DPI, Aura lighting) is not ported yet; port from
/// libratbag src/driver-asus.c. Discovery and UI still treat these as
/// unsupported until then.
/// </summary>
public sealed class AsusDevice : StubDeviceDriver
{
    public AsusDevice(ushort productId, string devicePath)
        : base(Vendors.Asus, productId, devicePath, Known.GetValueOrDefault(productId, "ASUS mouse")) { }

    public override Brand Brand => Brand.Asus;
    public override string ReferenceDriver => "libratbag src/driver-asus.c";

    /// <summary>Known ASUS product ids → model name (from libratbag/Piper svg-lookup).</summary>
    public static readonly Dictionary<ushort, string> Known = new()
    {
        [0x1877] = "ROG Gladius II Origin",
        [0x18B4] = "ROG Strix Carry",
        [0x18CD] = "ROG Gladius II Origin PNK",
        [0x18E1] = "ROG Strix Impact II",
        [0x1947] = "ROG Strix Impact II Wireless",
        [0x1949] = "ROG Strix Impact II Wireless",
        [0x195C] = "ROG Keris",
        [0x195E] = "ROG Keris Wireless",
        [0x1960] = "ROG Keris Wireless",
        [0x1A03] = "TUF Gaming M4 Air",
        [0x1A18] = "ROG Chakram X",
        [0x1A1A] = "ROG Chakram X",
        [0x1A68] = "ROG Keris II Wireless AimPoint",
        [0x1A92] = "ROG Harpe Ace Wireless",
        [0x1C56] = "TUF Gaming M4 Wireless",
        [0x1C57] = "TUF Gaming M4 Wireless",
    };
}
