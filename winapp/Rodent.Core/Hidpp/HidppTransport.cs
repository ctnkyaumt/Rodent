using HidSharp;
using Rodent.Core.Diagnostics;

namespace Rodent.Core.Hidpp;

/// <summary>
/// Low-level HID++ transport over a single Windows HID collection.
///
/// HID++ frames a request as: [reportId, deviceIndex, featureIndex, funcId|swId, params...].
///   reportId 0x10 = short (7 bytes), 0x11 = long (20 bytes).
/// On Windows, WriteFile for a HID output report must be exactly the collection's
/// OutputReportByteLength, so we always emit long (0x11) reports padded to that
/// length. HID++ 2.0 devices accept long requests universally.
///
/// The software id (low nibble of the 4th byte) distinguishes replies from
/// spontaneous notifications. Faithful port of Solaar's base.py request logic.
/// </summary>
public sealed class HidppTransport : IDisposable
{
    public const byte ShortReportId = 0x10;
    public const byte LongReportId = 0x11;

    // Direct (wired / USB) devices address themselves as 0xFF.
    public const byte DirectDeviceIndex = 0xFF;

    // A collection carrying HID++ long reports must be at least this long.
    public const int LongLen = 20;

    private readonly HidStream _stream;
    private readonly int _outLen;
    private readonly int _inLen;
    private readonly object _lock = new();
    private int _swId = 1;
    private bool _gone;

    public static bool Debug = false;
    public byte DeviceIndex { get; }

    private HidppTransport(HidStream stream, int outLen, int inLen, byte deviceIndex)
    {
        _stream = stream;
        _stream.ReadTimeout = 1000;
        _outLen = outLen;
        _inLen = inLen;
        DeviceIndex = deviceIndex;
    }

    public static HidppTransport? TryOpen(HidDevice device, byte deviceIndex = DirectDeviceIndex)
    {
        var cfg = new OpenConfiguration();
        cfg.SetOption(OpenOption.Exclusive, false);
        if (!device.TryOpen(cfg, out HidStream stream))
            return null;
        int outLen = Math.Max(device.GetMaxOutputReportLength(), LongLen);
        int inLen = Math.Max(device.GetMaxInputReportLength(), LongLen);
        return new HidppTransport(stream, outLen, inLen, deviceIndex);
    }

    private byte NextSwId()
    {
        _swId = _swId >= 0x0F ? 1 : _swId + 1; // cycle 1..15, never 0
        return (byte)_swId;
    }

    /// <summary>
    /// Perform a feature call. featureIndex is the device-local index (from ROOT).
    /// funcId is the function already in high-nibble form (0x00, 0x10, 0x20, 0x30, ...),
    /// matching Solaar's read_fnid/write_fnid convention; the low nibble carries the
    /// software id. Returns the reply payload (bytes after the 4-byte header), or null.
    /// </summary>
    public byte[]? Request(byte featureIndex, byte funcId, params byte[] parameters)
    {
        lock (_lock)
        {
            // Once the device has vanished every call would burn the full 2 s
            // timeout; fail fast instead so a rescan can clean up.
            if (_gone) return null;

            byte swId = NextSwId();
            byte funcByte = (byte)((funcId & 0xF0) | swId);

            var buf = new byte[_outLen];
            buf[0] = LongReportId;
            buf[1] = DeviceIndex;
            buf[2] = featureIndex;
            buf[3] = funcByte;
            for (int i = 0; i < parameters.Length && 4 + i < _outLen; i++)
                buf[4 + i] = parameters[i];

            DrainInput();
            try
            {
                _stream.Write(buf);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Log.Debug("HID write failed, device likely gone: " + ex.Message);
                _gone = true;
                return null;
            }
            Log.Frame($">> feat={featureIndex:X2} func={funcByte:X2}", buf.AsSpan(0, Math.Min(8, buf.Length)));
            if (Debug)
                Console.WriteLine($"  >> W feat={featureIndex:X2} func={funcByte:X2} : {string.Join(" ", buf.Take(8).Select(x => x.ToString("X2")))}");

            var deadline = DateTime.UtcNow.AddMilliseconds(2000);
            while (DateTime.UtcNow < deadline)
            {
                byte[]? reply = ReadReport();
                if (_gone) return null;
                if (reply != null)
                    Log.Frame("<<", reply.AsSpan(0, Math.Min(8, reply.Length)));
                if (Debug && reply != null)
                    Console.WriteLine($"  << R {string.Join(" ", reply.Take(8).Select(x => x.ToString("X2")))}");
                if (reply == null || reply.Length < 4)
                    continue;
                if (reply[1] != DeviceIndex && reply[1] != (DeviceIndex ^ 0xFF))
                    continue;

                // HID++ 1.0 error (short): byte2 == 0x8F echoing our feature+func
                if (reply[0] == ShortReportId && reply[2] == 0x8F &&
                    reply.Length > 4 && reply[3] == featureIndex && reply[4] == funcByte)
                    return null;
                // HID++ 2.0 error: byte2 == 0xFF echoing our feature+func
                if (reply[2] == 0xFF && reply.Length > 4 && reply[3] == featureIndex && reply[4] == funcByte)
                    return null;

                if (reply[2] == featureIndex && reply[3] == funcByte)
                    return reply[4..];
                // else: a notification or unrelated reply; keep waiting
            }
            return null;
        }
    }

    private byte[]? ReadReport()
    {
        try
        {
            var buf = new byte[_inLen];
            int n = _stream.Read(buf, 0, buf.Length);
            return n <= 0 ? null : buf[..n];
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The mouse was unplugged (Win32 1167 "device not connected") or the
            // handle is gone. Treat it as no reply — the caller's request times
            // out and the device drops off the next scan.
            Log.Debug("HID read failed, device likely gone: " + ex.Message);
            _gone = true;
            return null;
        }
    }

    /// <summary>True once a read failed because the device went away.</summary>
    public bool IsGone => _gone;

    private void DrainInput()
    {
        int saved;
        try { saved = _stream.ReadTimeout; _stream.ReadTimeout = 0; }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { _gone = true; return; }
        try
        {
            var tmp = new byte[_inLen];
            while (true)
            {
                try
                {
                    if (_stream.Read(tmp, 0, tmp.Length) <= 0) break;
                }
                catch (TimeoutException) { break; }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    _gone = true;
                    break;
                }
            }
        }
        finally { try { _stream.ReadTimeout = saved; } catch { } }
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch { }
    }
}
