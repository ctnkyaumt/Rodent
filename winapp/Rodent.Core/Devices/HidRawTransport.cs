using HidSharp;

namespace Rodent.Core.Devices;

/// <summary>
/// Plain HID feature/output-report transport for the non-HID++ brands
/// (SteelSeries, SinoWealth/Glorious, …). Those protocols exchange fixed-length
/// HID feature reports (Get/Set Feature) or output/input reports rather than the
/// HID++ request/reply framing that <see cref="Rodent.Core.Hidpp.HidppTransport"/>
/// uses. Byte 0 of every buffer is the HID report id.
/// </summary>
public sealed class HidRawTransport : IDisposable
{
    private readonly HidStream _stream;
    public int FeatureLength { get; }
    public int OutputLength { get; }
    public int InputLength { get; }

    private HidRawTransport(HidStream stream, int feature, int output, int input)
    {
        _stream = stream;
        _stream.ReadTimeout = 1000;
        FeatureLength = feature;
        OutputLength = output;
        InputLength = input;
    }

    public static HidRawTransport? TryOpen(HidDevice device)
    {
        var cfg = new OpenConfiguration();
        cfg.SetOption(OpenOption.Exclusive, false);
        if (!device.TryOpen(cfg, out HidStream stream))
            return null;
        int feat = SafeLen(device.GetMaxFeatureReportLength);
        int outl = SafeLen(device.GetMaxOutputReportLength);
        int inl = SafeLen(device.GetMaxInputReportLength);
        return new HidRawTransport(stream, feat, outl, inl);
    }

    private static int SafeLen(Func<int> get) { try { return get(); } catch { return 0; } }

    /// <summary>Set Feature report. `data` includes the report id at [0]; it is padded
    /// to the device's feature length.</summary>
    public bool SetFeature(byte[] data)
    {
        try { _stream.SetFeature(Pad(data, FeatureLength)); return true; }
        catch { return false; }
    }

    /// <summary>Get Feature report for the given report id. Returns the raw buffer
    /// (id at [0]) or null.</summary>
    public byte[]? GetFeature(byte reportId, int length = 0)
    {
        try
        {
            var buf = new byte[length > 0 ? length : FeatureLength];
            buf[0] = reportId;
            _stream.GetFeature(buf);
            return buf;
        }
        catch { return null; }
    }

    /// <summary>Write an output report (id at [0]), padded to the output length.</summary>
    public bool Write(byte[] data)
    {
        try { _stream.Write(Pad(data, OutputLength)); return true; }
        catch { return false; }
    }

    /// <summary>Read one input report, or null on timeout.</summary>
    public byte[]? Read()
    {
        try
        {
            var buf = new byte[InputLength];
            int n = _stream.Read(buf, 0, buf.Length);
            return n <= 0 ? null : buf[..n];
        }
        catch (TimeoutException) { return null; }
        catch { return null; }
    }

    private static byte[] Pad(byte[] data, int length)
    {
        if (length <= 0 || data.Length == length) return data;
        var buf = new byte[length];
        Array.Copy(data, buf, Math.Min(data.Length, length));
        return buf;
    }

    public void Dispose() => _stream.Dispose();
}
