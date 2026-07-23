using Rodent.Core.Model;

namespace Rodent.Core.Devices;

/// <summary>
/// Glorious and other SinoWealth-based mice (vendor 0x258A). Ported from libratbag
/// src/driver-sinowealth.c. These exchange fixed 520-byte HID feature reports:
/// report id 0x05 = command, 0x04/0x06 = (long) config.
///
/// STATUS: real transport + config/firmware read are ported. DPI encoding is
/// sensor-dependent (raw = DPI/100 for PMW3389, DPI/100-1 for PMW3360/3327), and
/// selecting the wrong sensor writes wrong DPI — so on this untested-in-Rodent
/// hardware, writes stay off; reads/identification are safe.
/// </summary>
public sealed class GloriousDevice : IDeviceDriver
{
    // Report ids (SINOWEALTH_REPORT_ID_*).
    private const byte ReportCmd = 0x05;
    private const byte ReportConfig = 0x04;
    private const byte ReportConfigLong = 0x06;

    // Command ids.
    private const byte CmdFirmware = 0x01;
    private const byte CmdGetConfig1 = 0x11;

    private const int ConfigLength = 520;

    private HidRawTransport? _io;
    private readonly List<InfoItem> _info = new();

    public GloriousDevice(ushort productId, string devicePath)
    {
        ProductId = productId;
        DevicePath = devicePath;
        Name = Known.GetValueOrDefault(productId, "SinoWealth mouse");
    }

    public Brand Brand => Brand.Glorious;
    public ushort VendorId => Vendors.Glorious;
    public ushort ProductId { get; }
    public string DevicePath { get; }
    public string Name { get; private set; }
    public string Kind => "mouse";
    public DeviceSupport Support => DeviceSupport.Untested;

    public IReadOnlyList<Setting> Settings => Array.Empty<Setting>();
    public IReadOnlyList<InfoItem> Info => _info;

    internal void Attach(HidRawTransport io) => _io = io;

    public bool Initialize()
    {
        // Recognised by vid/pid; no report is sent until the model is verified
        // and a transport is attached (see DeviceFactory).
        if (_io != null)
        {
            string? fw = ReadFirmware();
            if (fw != null) _info.Add(new InfoItem("Firmware", fw));
        }
        return true;
    }

    /// <summary>Firmware string: send CMD(0x01), read the config feature report,
    /// the version lives in the first bytes after the header.</summary>
    private string? ReadFirmware()
    {
        if (_io == null) return null;
        // Ask for the firmware/version block.
        if (!_io.SetFeature(new byte[] { ReportCmd, CmdFirmware }))
            return null;
        byte[]? cfg = _io.GetFeature(ReportConfig, ConfigLength) ?? _io.GetFeature(ReportConfigLong, ConfigLength);
        if (cfg == null || cfg.Length < 4) return null;
        // libratbag reads an ASCII version string from the reply; surface printable
        // bytes, else a short hex fingerprint (layout unverified on real hardware).
        var ascii = new string(cfg.Skip(2).Take(8)
            .Where(b => b >= 0x20 && b < 0x7F).Select(b => (char)b).ToArray());
        return ascii.Trim().Length >= 3 ? ascii.Trim() : $"{cfg[2]:X2}.{cfg[3]:X2}";
    }

    /// <summary>Ported DPI encode (kept for future write support). raw is what goes
    /// into the config DPI slot; the /100 vs /100-1 split is per sensor.</summary>
    private static byte EncodeDpi(int dpi, bool sensorMinusOne) =>
        (byte)Math.Clamp((dpi / 100) - (sensorMinusOne ? 1 : 0), 0, 0xFF);

    /// <summary>Read the raw 520-byte config (profile 1). For future settings work.</summary>
    private byte[]? ReadConfig()
    {
        if (_io == null) return null;
        if (!_io.SetFeature(new byte[] { ReportCmd, CmdGetConfig1 })) return null;
        return _io.GetFeature(ReportConfig, ConfigLength);
    }

    public void Dispose() { _io?.Dispose(); _io = null; }

    public static readonly Dictionary<ushort, string> Known = new()
    {
        [0x0012] = "SN-Tech T3",           // generic SinoWealth reference board
        [0x0033] = "Glorious Model D",
        [0x0036] = "Glorious Model O",
    };
}
