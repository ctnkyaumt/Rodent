namespace Rodent.Core.Devices;

/// <summary>
/// Mars Gaming mice (vendor 0x04D9, Holtek-based). STUB — protocol not ported
/// yet; port from libratbag src/driver-marsgaming.c.
/// </summary>
public sealed class MarsGamingDevice : StubDeviceDriver
{
    public MarsGamingDevice(ushort productId, string devicePath)
        : base(Vendors.MarsGaming, productId, devicePath, Known.GetValueOrDefault(productId, "Mars Gaming mouse")) { }

    public override Brand Brand => Brand.MarsGaming;
    public override string ReferenceDriver => "libratbag src/driver-marsgaming.c";

    public static readonly Dictionary<ushort, string> Known = new()
    {
        [0xFA58] = "Mars Gaming MM4",
    };
}
