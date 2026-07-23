namespace Rodent.Core.Devices;

/// <summary>
/// Mars Gaming mice (vendor 0x04D9, Holtek-based). STUB — no upstream libratbag
/// driver exists; the protocol would have to be reverse-engineered (the MM4 does
/// appear in Piper's device-svg list, which is why it is recognised here).
/// </summary>
public sealed class MarsGamingDevice : StubDeviceDriver
{
    public MarsGamingDevice(ushort productId, string devicePath)
        : base(Vendors.MarsGaming, productId, devicePath, Known.GetValueOrDefault(productId, "Mars Gaming mouse")) { }

    public override Brand Brand => Brand.MarsGaming;
    public override string ReferenceDriver => "(none — needs reverse engineering)";

    public static readonly Dictionary<ushort, string> Known = new()
    {
        [0xFA58] = "Mars Gaming MM4",
    };
}
