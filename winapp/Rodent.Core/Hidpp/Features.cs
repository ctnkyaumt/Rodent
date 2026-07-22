namespace Rodent.Core.Hidpp;

/// <summary>Well-known HID++ 2.0 feature IDs (subset; expand as devices are added).</summary>
public static class FeatureId
{
    public const ushort Root = 0x0000;
    public const ushort FeatureSet = 0x0001;
    public const ushort DeviceFwVersion = 0x0003;
    public const ushort DeviceName = 0x0005;
    public const ushort BatteryStatus = 0x1000;   // legacy battery (unified below preferred)
    public const ushort LedControl = 0x1300;      // non-color LED zones (on/breathing/...)
    public const ushort HiddenFeatures = 0x1E00;  // unlocks engineering/config writes
    public const ushort BatteryVoltage = 0x1001;
    public const ushort UnifiedBattery = 0x1004;
    public const ushort AdjustableDpi = 0x2201;
    public const ushort ReportRate = 0x8060;
    public const ushort ExtendedReportRate = 0x8061;
    public const ushort OnboardProfiles = 0x8100;
}

/// <summary>Resolves feature IDs to device-local indices via the ROOT feature (0x0000).</summary>
public sealed class FeatureTable
{
    private readonly HidppTransport _t;
    private readonly Dictionary<ushort, byte> _cache = new();

    public FeatureTable(HidppTransport transport) => _t = transport;

    /// <summary>Returns the device-local index for a feature, or 0 if the device lacks it.</summary>
    public byte GetIndex(ushort featureId)
    {
        if (featureId == FeatureId.Root)
            return 0; // ROOT is always index 0
        if (_cache.TryGetValue(featureId, out var idx))
            return idx;

        // ROOT.GetFeature(featureId) -> [featureIndex, featureType, featureVersion]
        byte[]? reply = _t.Request(0x00, 0x00, (byte)(featureId >> 8), (byte)(featureId & 0xFF));
        byte index = (reply != null && reply.Length >= 1) ? reply[0] : (byte)0;
        _cache[featureId] = index;
        return index;
    }

    public bool Has(ushort featureId) => GetIndex(featureId) != 0;

    /// <summary>Feature call by feature ID (resolves the index first). Null if unsupported or on error.</summary>
    public byte[]? Call(ushort featureId, byte funcId, params byte[] parameters)
    {
        byte index = GetIndex(featureId);
        if (index == 0 && featureId != FeatureId.Root)
            return null;
        return _t.Request(index, funcId, parameters);
    }
}
