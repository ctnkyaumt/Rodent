using HidSharp;
using Rodent.Core.Hidpp;

namespace Rodent.Core.Devices;

/// <summary>Enumerates Logitech HID++ devices attached to the system.</summary>
public static class DeviceManager
{
    public const int LogitechVendorId = 0x046D;

    // HID++ lives on a Logitech vendor-defined collection (usage page 0xFF00).
    private const int HidppUsagePage = 0xFF00;

    public static List<LogiDevice> Discover()
    {
        var result = new List<LogiDevice>();
        var seen = new HashSet<string>();

        foreach (var hid in DeviceList.Local.GetHidDevices(LogitechVendorId))
        {
            // Prefer the vendor HID++ collection; long-report collection can carry 20-byte frames.
            if (!LooksLikeHidpp(hid))
                continue;

            // Avoid opening the same physical device twice (multiple collections share a path prefix).
            string key = PhysicalKey(hid);
            if (!seen.Add(key))
                continue;

            var transport = HidppTransport.TryOpen(hid);
            if (transport == null)
                continue;

            var dev = new LogiDevice(transport, (ushort)hid.VendorID, (ushort)hid.ProductID, hid.DevicePath);
            if (dev.Initialize())
                result.Add(dev);
            else
                dev.Dispose();
        }
        return result;
    }

    private static bool LooksLikeHidpp(HidDevice hid)
    {
        try
        {
            // Must be able to carry 20-byte long reports.
            if (hid.GetMaxOutputReportLength() < HidppTransport.LongLen)
                return false;
            var rd = hid.GetReportDescriptor();
            foreach (var di in rd.DeviceItems)
                foreach (var usage in di.Usages.GetAllValues())
                    if ((usage >> 16) == HidppUsagePage)
                        return true;
            return false;
        }
        catch
        {
            return false;
        }
    }

    // Group collections belonging to the same physical device (strip the trailing &ColNN).
    private static string PhysicalKey(HidDevice hid)
    {
        string path = hid.DevicePath;
        int col = path.IndexOf("&col", StringComparison.OrdinalIgnoreCase);
        return col > 0 ? path[..col] : path;
    }
}
