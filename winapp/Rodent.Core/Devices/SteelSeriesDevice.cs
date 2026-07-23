using Rodent.Core.Model;

namespace Rodent.Core.Devices;

/// <summary>
/// SteelSeries gaming mice (vendor 0x1038). Ported from libratbag
/// src/driver-steelseries.c. The protocol has four command generations; the
/// per-model generation and DPI encoding come from libratbag's .device files.
///
/// STATUS: real transport + firmware read + command builders are ported, but
/// nothing here is hardware-verified (no SteelSeries device to test against), so
/// DPI/rate WRITES are intentionally not wired into the settings UI yet — the
/// encoded-value/report-collection choices need confirming on real hardware
/// before Rodent should push them. Reads/identification are safe.
/// </summary>
public sealed class SteelSeriesDevice : IDeviceDriver
{
    // Protocol generation (libratbag STEELSERIES_PROTO_*).
    private enum Proto { V1, V2, V3, V4 }

    private readonly Proto _proto;
    private HidRawTransport? _io;
    private readonly List<InfoItem> _info = new();

    public SteelSeriesDevice(ushort productId, string devicePath)
    {
        ProductId = productId;
        DevicePath = devicePath;
        Name = Known.GetValueOrDefault(productId, "SteelSeries mouse");
        _proto = ProtoFor(productId);
    }

    public Brand Brand => Brand.SteelSeries;
    public ushort VendorId => Vendors.SteelSeries;
    public ushort ProductId { get; }
    public string DevicePath { get; }
    public string Name { get; private set; }
    public string Kind => "mouse";
    public DeviceSupport Support => DeviceSupport.Untested;

    // Writes stay off until verified; reads populate Info only.
    public IReadOnlyList<Setting> Settings => Array.Empty<Setting>();
    public IReadOnlyList<InfoItem> Info => _info;

    internal void Attach(HidRawTransport io) => _io = io;

    public bool Initialize()
    {
        // Recognised by vid/pid. A transport is only attached once the model is
        // verified (see DeviceFactory); until then we don't send any report.
        if (_io != null)
        {
            string? fw = ReadFirmware();
            if (fw != null) _info.Add(new InfoItem("Firmware", fw));
        }
        return true;
    }

    // ---- ported command builders (libratbag driver-steelseries.c) -----------
    // Report layouts per generation. Kept as real code so completing write
    // support is a matter of wiring + hardware verification, not re-derivation.

    private const byte ReportShort = 0x00; // SteelSeries uses report id 0 (unnumbered)

    /// <summary>Firmware/version request opcode per generation.</summary>
    private byte FirmwareCmd => _proto switch { Proto.V2 => 0x90, _ => 0x10 };

    private string? ReadFirmware()
    {
        if (_io == null) return null;
        var buf = new byte[Math.Max(_io.OutputLength, 64)];
        buf[0] = ReportShort;
        buf[1] = FirmwareCmd;
        if (!_io.Write(buf)) return null;
        var reply = _io.Read();
        if (reply == null || reply.Length < 4) return null;
        // libratbag reads the version as BCD-ish bytes; report a hex fingerprint
        // rather than guess the exact layout for an untested model.
        return $"{reply[1]:X2}.{reply[2]:X2}";
    }

    /// <summary>Build a DPI write report (idx = 0-based stage). Not yet used —
    /// the raw value encoding is per-sensor and needs hardware confirmation.</summary>
    private byte[] BuildDpi(int idx, int rawValue)
    {
        var b = new byte[_proto == Proto.V1 ? 32 : 64];
        switch (_proto)
        {
            case Proto.V1: b[0] = 0x03; b[1] = (byte)(idx + 1); b[2] = (byte)rawValue; break;
            case Proto.V2: b[0] = 0x53; b[2] = (byte)(idx + 1); b[3] = (byte)rawValue; break;
            case Proto.V3: b[0] = 0x03; b[2] = (byte)(idx + 1); b[3] = (byte)rawValue; break;
            case Proto.V4: b[0] = 0x15; b[1] = (byte)(idx + 1); b[2] = (byte)rawValue; break;
        }
        return b;
    }

    /// <summary>Build a report-rate write. v2/v3 encode 1000/Hz; v1/v4 use a code.</summary>
    private byte[] BuildRate(int hz)
    {
        var b = new byte[_proto == Proto.V1 ? 32 : 64];
        switch (_proto)
        {
            case Proto.V2: b[0] = 0x54; b[2] = (byte)(hz > 0 ? 1000 / hz : 1); break;
            case Proto.V3: b[0] = 0x04; b[2] = (byte)(hz > 0 ? 1000 / hz : 1); break;
            case Proto.V1: b[0] = 0x04; b[2] = (byte)(hz > 0 ? 1000 / hz : 1); break;
            case Proto.V4: b[0] = 0x17; b[2] = (byte)(hz > 0 ? 1000 / hz : 1); break;
        }
        return b;
    }

    private byte[] BuildSave() => _proto switch
    {
        Proto.V2 => new byte[] { 0x59 },
        _ => new byte[] { 0x09 },
    };

    public void Dispose() { _io?.Dispose(); _io = null; }

    // Generation per model (from libratbag .device files). Older sensors = v1;
    // TrueMove-era (Rival 310/600, Sensei 310/Ten) = v3.
    private static Proto ProtoFor(ushort pid) => pid switch
    {
        0x1720 or 0x1722 or 0x1724 or 0x1832 => Proto.V3,
        _ => Proto.V1,
    };

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
