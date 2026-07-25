using Rodent.Core.Model;

namespace Rodent.Core.Devices;

/// <summary>
/// Base for brands whose protocol isn't ported yet. It carries identity (so the
/// device can be recognised and named) but exposes no settings.
///
/// Porting a brand means implementing the capability interfaces its protocol
/// covers — <see cref="IButtonDevice"/>, <see cref="ILightingDevice"/>,
/// <see cref="IDpiDevice"/>, <see cref="IMacroDevice"/> — at which point the
/// matching GUI tabs and Rodent.Probe commands start working for it with no
/// changes anywhere else.
/// </summary>
public abstract class StubDeviceDriver : IDeviceDriver
{
    protected StubDeviceDriver(ushort vendorId, ushort productId, string devicePath, string name, string kind = "mouse")
    {
        VendorId = vendorId;
        ProductId = productId;
        DevicePath = devicePath;
        Name = name;
        Kind = kind;
    }

    public abstract Brand Brand { get; }

    /// <summary>libratbag driver this brand's protocol should be ported from.</summary>
    public abstract string ReferenceDriver { get; }

    public ushort VendorId { get; }
    public ushort ProductId { get; }
    public string DevicePath { get; }
    public string Name { get; protected set; }
    public string Kind { get; protected set; }

    // Not verified because it isn't even connected yet.
    public DeviceSupport Support => DeviceSupport.Untested;

    public IReadOnlyList<Setting> Settings => Array.Empty<Setting>();
    public IReadOnlyList<InfoItem> Info => Array.Empty<InfoItem>();

    /// <summary>Recognised (so it lists), but nothing is controllable yet — the
    /// UI shows an "experimental" note and no settings. Override when a real
    /// protocol lands.</summary>
    public virtual bool Initialize() => true;

    public virtual void Dispose() { }
}
