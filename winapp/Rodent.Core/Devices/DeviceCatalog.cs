namespace Rodent.Core.Devices;

/// <summary>How well Rodent is known to work with a given device.</summary>
public enum DeviceSupport
{
    /// <summary>Hardware-verified end to end (reads and flash writes).</summary>
    Verified,

    /// <summary>Recognised by name, but nothing has been tested on real hardware.
    /// Generic feature reads/writes (DPI, report rate, lighting) usually work
    /// because they come from HID++ feature discovery; onboard flash writes assume
    /// the G402 memory layout and are UNPROVEN here — treat as experimental.</summary>
    Untested,
}

/// <summary>
/// Per-device onboard-memory layout — the byte offsets inside a profile sector
/// that are NOT discoverable over HID++ and so must be known per model. Only the
/// LED entry offsets vary meaningfully between the devices modelled so far; the
/// DPI block and button array follow the standard libratbag onboard-profile
/// layout. This is the hook future device work fills in (see [[DeviceCatalog]]).
/// </summary>
public sealed record OnboardLayout(int[] LedOffsets)
{
    /// <summary>G402 Hyperion Fury — hardware-verified (sector-1 dump).</summary>
    public static readonly OnboardLayout G402 = new(new[] { 0x0D0, 0x0DB, 0x0E6, 0x0F1 });
}

/// <summary>What Rodent knows about one device model, keyed by USB product id.</summary>
public sealed record DeviceSpec(string Name, DeviceSupport Support, OnboardLayout? Onboard = null);

/// <summary>
/// Registry of known Logitech HID++ devices. Discovery itself is generic
/// (<see cref="DeviceManager"/> finds any HID++ device), so this catalog is not
/// required to connect — it supplies a friendly fallback name, a support level
/// for the UI's "untested model" warning, and the per-device onboard layout.
///
/// Only the G402 is Verified. Every other entry is a STUB for future work: the
/// names are ported from libratbag (data/devices/*.device) and Solaar so the
/// device shows correctly, but its onboard layout is null (falls back to the
/// G402 layout for reads, and the UI warns before any flash write). Fill in a
/// model's OnboardLayout and flip it to Verified once tested on real hardware.
/// </summary>
public static class DeviceCatalog
{
    public const int LogitechVendorId = 0x046D;

    private static uint Key(ushort vid, ushort pid) => ((uint)vid << 16) | pid;

    public static DeviceSpec? Lookup(ushort vendorId, ushort productId) =>
        Known.TryGetValue(Key(vendorId, productId), out var spec) ? spec : null;

    private static DeviceSpec Stub(string name) => new(name, DeviceSupport.Untested);

    // key = (vid << 16) | pid. Logitech only — other vendors in the SVG art map
    // (SteelSeries, Roccat, ASUS, Glorious) speak their own protocols and never
    // reach the HID++ engine.
    public static readonly Dictionary<uint, DeviceSpec> Known = new()
    {
        // ---- Verified ----
        [0x046D_C07E] = new("G402 Hyperion Fury", DeviceSupport.Verified, OnboardLayout.G402),

        // ---- Stubs: wired gaming mice (future work) ----
        [0x046D_C07D] = Stub("G502 Proteus Core"),
        [0x046D_C332] = Stub("G502 Proteus Spectrum"),
        [0x046D_C08B] = Stub("G502 SE Hero"),
        [0x046D_C08D] = Stub("G502 Hero"),
        [0x046D_C099] = Stub("G502 X"),
        [0x046D_C07F] = Stub("G303 Daedalus Apex"),
        [0x046D_C080] = Stub("G303"),
        [0x046D_C097] = Stub("G303 Shroud Edition"),
        [0x046D_C082] = Stub("G403 Prodigy"),
        [0x046D_C083] = Stub("G403 Prodigy Wireless"),
        [0x046D_C08F] = Stub("G403 Hero"),
        [0x046D_C084] = Stub("G102/G203 Prodigy"),
        [0x046D_C092] = Stub("G102/G203 Lightsync"),
        [0x046D_C09D] = Stub("G203 Lightsync"),
        [0x046D_C085] = Stub("G Pro (wired)"),
        [0x046D_C08C] = Stub("G Pro Hero"),
        [0x046D_C088] = Stub("G Pro Wireless"),
        [0x046D_C094] = Stub("G Pro X Superlight"),
        [0x046D_C08E] = Stub("MX518 Legendary"),
        [0x046D_C24A] = Stub("G600 MMO"),
        [0x046D_C246] = Stub("G300"),
        [0x046D_C068] = Stub("G500"),
        [0x046D_C24E] = Stub("G500s"),
        [0x046D_C06B] = Stub("G700"),
        [0x046D_C07C] = Stub("G700s"),
        [0x046D_C531] = Stub("G700s (receiver)"),
        [0x046D_C093] = Stub("M500s"),
        [0x046D_C048] = Stub("G9"),
        [0x046D_C066] = Stub("G9x"),
        [0x046D_C249] = Stub("G9x (receiver)"),

        // ---- Stubs: Lightspeed / wireless mice (wired-mode product ids) ----
        [0x046D_402C] = Stub("G602"),
        [0x046D_405D] = Stub("G403 Wireless"),
        [0x046D_4053] = Stub("G900 Chaos Spectrum"),
        [0x046D_4067] = Stub("G903"),
        [0x046D_4087] = Stub("G903 Hero"),
        [0x046D_4070] = Stub("G703"),
        [0x046D_4086] = Stub("G703 Hero"),
        [0x046D_4074] = Stub("G Pro / G304 Lightspeed"),
        [0x046D_4079] = Stub("G Pro Wireless"),
        [0x046D_4085] = Stub("G604 Lightspeed"),
        [0x046D_4093] = Stub("G Pro X Superlight"),
        [0x046D_407F] = Stub("G502 Lightspeed"),
        [0x046D_4099] = Stub("G502 X Plus"),
        [0x046D_409F] = Stub("G502 X"),
        [0x046D_409D] = Stub("G705"),
        [0x046D_B02E] = Stub("G705 (Bluetooth)"),

        // ---- Stubs: MX / productivity mice (HID++, no gaming onboard profiles) ----
        [0x046D_4041] = Stub("MX Master"),
        [0x046D_4060] = Stub("MX Master"),
        [0x046D_4071] = Stub("MX Master 2S"),
        [0x046D_4069] = Stub("MX Master 2S"),
        [0x046D_4082] = Stub("MX Master 3"),
        [0x046D_B023] = Stub("MX Master 3"),
        [0x046D_B034] = Stub("MX Master 3S"),
        [0x046D_404A] = Stub("MX Anywhere 2"),
        [0x046D_406A] = Stub("MX Anywhere 2S"),
        [0x046D_B025] = Stub("MX Anywhere 3"),
        [0x046D_407B] = Stub("MX Vertical"),
        [0x046D_406F] = Stub("MX Ergo"),
        [0x046D_405E] = Stub("M720 Triathlon"),
    };
}
