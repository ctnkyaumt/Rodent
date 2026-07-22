using Rodent.Core.Hidpp;
using Rodent.Core.Model;

namespace Rodent.Core.Devices;

/// <summary>
/// A connected Logitech HID++ 2.0 device. Discovers supported features and
/// exposes them as generic Settings. Feature logic is a direct port of the
/// corresponding Solaar templates (lib/logitech_receiver/settings_templates.py).
/// </summary>
public sealed class LogiDevice : IDisposable
{
    private readonly HidppTransport _transport;
    private readonly FeatureTable _features;

    /// <summary>Exposed for debugging/probing raw feature calls.</summary>
    public FeatureTable Features => _features;

    public string Name { get; private set; } = "Logitech device";
    public string Kind { get; private set; } = "device";
    public ushort VendorId { get; }
    public ushort ProductId { get; }
    public string DevicePath { get; }
    public List<Setting> Settings { get; } = new();
    public List<InfoItem> Info { get; } = new();
    public List<OnboardProfiles.ButtonAction> Buttons { get; } = new();
    public List<LedControl.Zone> Leds { get; } = new();

    public LogiDevice(HidppTransport transport, ushort vendorId, ushort productId, string devicePath)
    {
        _transport = transport;
        _features = new FeatureTable(transport);
        VendorId = vendorId;
        ProductId = productId;
        DevicePath = devicePath;
    }

    // Serializes flash read-modify-write cycles. The transport lock only covers a
    // single HID request; two interleaved sector rewrites would lose updates.
    private readonly object _flashLock = new();

    /// <summary>True when the mouse is in onboard mode (executes stored profiles).
    /// In host mode the device ignores onboard button mappings entirely.</summary>
    public bool IsOnboardMode()
    {
        byte[]? r = _features.Call(FeatureId.OnboardProfiles, 0x20);
        return r != null && r.Length >= 1 && r[0] == 0x01;
    }

    /// <summary>Switch the mouse to onboard mode so assignments/macros actually run.</summary>
    public bool EnableOnboardMode()
    {
        _features.Call(FeatureId.OnboardProfiles, 0x10, 0x01);
        return IsOnboardMode();
    }

    /// <summary>Raw 4-byte action of a button in the active profile (for backups).</summary>
    public byte[]? ReadButtonBytes(int index1Based)
    {
        lock (_flashLock)
        {
            try { return OnboardProfiles.ReadButtonBytes(_features, index1Based); }
            catch { return null; }
        }
    }

    /// <summary>Reassign a button (1-based) to a 4-byte action. Returns (success, new label).</summary>
    public (bool ok, string label) RemapButton(int index1Based, byte[] action)
    {
        lock (_flashLock)
        {
            try
            {
                bool ok = OnboardProfiles.WriteButton(_features, index1Based, action);
                var list = OnboardProfiles.ReadButtons(_features);
                var found = list.FirstOrDefault(b => b.Index == index1Based);
                // refresh cached list too
                Buttons.Clear();
                Buttons.AddRange(list);
                return (ok, found?.Label ?? "?");
            }
            catch
            {
                return (false, "?"); // device gone / I/O failure
            }
        }
    }

    /// <summary>
    /// Store a macro in a free sector and point a button at it.
    /// On failure, error is a short human-readable reason.
    /// </summary>
    public (bool ok, int? sector, int? address, string? error) AssignMacro(
        int index1Based, IReadOnlyList<Macro.Step> steps, Macro.RepeatMode repeat = Macro.RepeatMode.Once)
    {
        lock (_flashLock)
        {
            try
            {
                int? sector = OnboardProfiles.FindMacroSector(_features);
                if (sector == null) return (false, null, null, "no free onboard sector");

                byte[] macroBytes = Macro.Encode(steps, repeat);
                var (wrote, address, error) = OnboardProfiles.WriteMacroInto(_features, sector.Value, macroBytes);
                if (!wrote || address == null) return (false, sector, address, error ?? "write failed");

                bool ok = OnboardProfiles.WriteButton(_features, index1Based,
                    OnboardProfiles.MacroButtonBytes(sector.Value, address.Value));
                Buttons.Clear();
                Buttons.AddRange(OnboardProfiles.ReadButtons(_features));
                return (ok, sector, address, ok ? null : "couldn't point the button at the macro");
            }
            catch
            {
                return (false, null, null, "device I/O failed");
            }
        }
    }

    /// <summary>Probe ROOT and, if the device answers, read name and build settings.</summary>
    public bool Initialize()
    {
        // ROOT.getFeature(FEATURE_SET) is a reliable liveness ping.
        var ping = _features.Call(FeatureId.Root, 0x00,
            (byte)(FeatureId.FeatureSet >> 8), (byte)(FeatureId.FeatureSet & 0xFF));
        if (ping == null)
            return false;

        Name = ReadDeviceName() ?? Name;
        Kind = ReadKind() ?? Kind;
        BuildInfo();
        BuildSettings();
        try { Buttons.AddRange(OnboardProfiles.ReadButtons(_features)); }
        catch { /* onboard profile read is best-effort */ }
        try { Leds.AddRange(LedControl.Read(_features)); }
        catch { /* LED info is best-effort */ }
        return true;
    }

    // ---- profile-stored lighting + DPI (the G HUB way) ----------------------

    public ProfileEdit.LightingConfig? ReadLighting()
    {
        lock (_flashLock) { try { return ProfileEdit.ReadLighting(_features); } catch { return null; } }
    }

    /// <summary>Apply lighting. persist=false drives the LEDs only (no flash wear),
    /// which is what per-app profile switching uses.</summary>
    public bool WriteLighting(ProfileEdit.LightingConfig cfg, bool persist = true)
    {
        lock (_flashLock) { try { return ProfileEdit.WriteLighting(_features, cfg, persist); } catch { return false; } }
    }

    public ProfileEdit.DpiConfig? ReadDpiProfile()
    {
        lock (_flashLock) { try { return _dpiCache = ProfileEdit.ReadDpi(_features); } catch { return null; } }
    }

    public bool WriteDpiProfile(ProfileEdit.DpiConfig cfg)
    {
        lock (_flashLock)
        {
            try
            {
                bool ok = ProfileEdit.WriteDpi(_features, cfg);
                if (ok) _dpiCache = cfg;
                return ok;
            }
            catch { return false; }
        }
    }

    // ---- host-side DPI actions (software profile bindings) ------------------

    private ProfileEdit.DpiConfig? _dpiCache;  // slot table; a flash read per press would be slow
    private int _sniperRestore = -1;
    private int _blinking;                     // 1 while a strip blink is running

    /// <summary>
    /// Run a DPI action from a software binding: "DPI +" / "DPI -" / "Cycle DPI"
    /// step through the profile's DPI slots live (0x2201, no flash), "DPI Shift
    /// (sniper)" drops to the shift slot while held and restores on release.
    /// </summary>
    public void DpiAction(string action, bool down)
    {
        try
        {
            var cfg = _dpiCache ?? ReadDpiProfile();
            if (cfg == null) return;
            var slots = cfg.Slots.Where(s => s > 0).ToArray();
            if (slots.Length == 0) return;

            byte[]? r = _features.Call(FeatureId.AdjustableDpi, 0x20, 0x00);
            int cur = r != null && r.Length >= 3 ? (r[1] << 8) | r[2] : slots[0];

            if (action.Contains("Shift"))
            {
                if (down)
                {
                    int shift = cfg.Slots[Math.Clamp(cfg.ShiftIndex, 0, 4)];
                    if (shift <= 0) return;
                    _sniperRestore = cur;
                    SetDpiLive(shift);
                }
                else if (_sniperRestore > 0)
                {
                    SetDpiLive(_sniperRestore);
                    _sniperRestore = -1;
                }
                return; // sniper is silent — no blink while aiming
            }
            if (!down) return;

            // Nearest slot to the live value, then step.
            int idx = 0;
            for (int i = 1; i < slots.Length; i++)
                if (Math.Abs(slots[i] - cur) < Math.Abs(slots[idx] - cur)) idx = i;
            idx = action switch
            {
                "DPI +" => Math.Min(idx + 1, slots.Length - 1),
                "DPI -" => Math.Max(idx - 1, 0),
                _ => (idx + 1) % slots.Length, // Cycle DPI
            };
            SetDpiLive(slots[idx]);

            // Same feedback as a Sensitivity-tab apply, but never stack blinks
            // when the button is mashed.
            if (Interlocked.CompareExchange(ref _blinking, 1, 0) == 0)
            {
                try { LedControl.BlinkStrip(_features); }
                finally { _blinking = 0; }
            }
        }
        catch { /* device I/O — a missed DPI step is harmless */ }
    }

    private void SetDpiLive(int dpi) =>
        _features.Call(FeatureId.AdjustableDpi, 0x30, 0x00, (byte)(dpi >> 8), (byte)(dpi & 0xFF));

    /// <summary>Allowed DPI values reported by the sensor (for slider snapping).</summary>
    public List<int> DpiChoices()
    {
        try { return ReadDpiList(); } catch { return new List<int>(); }
    }

    private static readonly string[] KindNames =
        { "keyboard", "remote control", "numpad", "mouse", "touchpad", "trackball", "presenter", "receiver" };

    private string? ReadKind()
    {
        // DEVICE_NAME 0x0005 func 0x20 -> [kindCode]
        byte[]? r = _features.Call(FeatureId.DeviceName, 0x20);
        if (r == null || r.Length < 1) return null;
        return r[0] < KindNames.Length ? KindNames[r[0]] : null;
    }

    private void BuildInfo()
    {
        Info.Add(new InfoItem("Type", Kind));
        var fw = ReadFirmware();
        if (fw != null) Info.Add(new InfoItem("Firmware", fw));
        var bat = ReadBattery();
        if (bat != null) Info.Add(new InfoItem("Battery", bat));
    }

    // ---- DEVICE_FW_VERSION 0x0003 ---------------------------------------------
    private string? ReadFirmware()
    {
        byte[]? count = _features.Call(FeatureId.DeviceFwVersion, 0x00);
        if (count == null || count.Length < 1) return null;
        int entities = count[0];
        for (int i = 0; i < entities; i++)
        {
            // func 0x10(index) -> [type, prefix(3 ascii), major(bcd), minor(bcd), build(2)]
            byte[]? e = _features.Call(FeatureId.DeviceFwVersion, 0x10, (byte)i);
            if (e == null || e.Length < 8) continue;
            if ((e[0] & 0x0F) != 0) continue; // 0 = main firmware
            // [type, prefix(3 ascii), major(bcd), minor(bcd), build(2)]
            string prefix = System.Text.Encoding.ASCII.GetString(new[] { e[1], e[2], e[3] }).Trim();
            int build = (e[6] << 8) | e[7];
            return $"{prefix} {e[4]:X2}.{e[5]:X2}.B{build:X4}".Trim();
        }
        return null;
    }

    // ---- Battery: UNIFIED_BATTERY 0x1004 then legacy BATTERY_STATUS 0x1000 -----
    private static readonly string[] BatteryStatus =
        { "discharging", "recharging", "almost full", "full", "slow recharge", "invalid battery", "thermal error" };

    private string? ReadBattery()
    {
        if (_features.Has(FeatureId.UnifiedBattery))
        {
            byte[]? r = _features.Call(FeatureId.UnifiedBattery, 0x10);
            if (r != null && r.Length >= 3)
            {
                int pct = r[0];
                int level = r[1];
                string approx = level switch { 8 => "full", 4 => "good", 2 => "low", 1 => "critical", _ => "" };
                string st = r[2] < BatteryStatus.Length ? BatteryStatus[r[2]] : "";
                string charge = pct > 0 ? $"{pct}%" : approx;
                return string.IsNullOrEmpty(st) ? charge : $"{charge} ({st})";
            }
        }
        if (_features.Has(FeatureId.BatteryStatus))
        {
            byte[]? r = _features.Call(FeatureId.BatteryStatus, 0x00);
            if (r != null && r.Length >= 3)
            {
                int pct = r[0];
                string st = r[2] < BatteryStatus.Length ? BatteryStatus[r[2]] : "";
                string charge = pct > 0 ? $"{pct}%" : "unknown";
                return string.IsNullOrEmpty(st) ? charge : $"{charge} ({st})";
            }
        }
        return null;
    }

    private string? ReadDeviceName()
    {
        // DEVICE_NAME 0x0005: func 0x00 -> [length]; func 0x10(offset) -> chars.
        byte[]? count = _features.Call(FeatureId.DeviceName, 0x00);
        if (count == null || count.Length < 1)
            return null;
        int len = count[0];
        var chars = new List<byte>();
        while (chars.Count < len)
        {
            byte[]? chunk = _features.Call(FeatureId.DeviceName, 0x10, (byte)chars.Count);
            if (chunk == null || chunk.Length == 0)
                break;
            foreach (var b in chunk)
            {
                if (chars.Count >= len || b == 0) break;
                chars.Add(b);
            }
            if (chunk.All(b => b == 0)) break;
        }
        return chars.Count > 0 ? System.Text.Encoding.ASCII.GetString(chars.ToArray()) : null;
    }

    private void BuildSettings()
    {
        BuildOnboardProfiles();
        BuildDpi();
        BuildReportRate();
    }

    // ---- ONBOARD_PROFILES 0x8100 (mode toggle) --------------------------------
    private void BuildOnboardProfiles()
    {
        if (!_features.Has(FeatureId.OnboardProfiles))
            return;
        // Confirm we can read the current mode before exposing the control.
        if (_features.Call(FeatureId.OnboardProfiles, 0x20) == null)
            return;

        _Settings_Add(new ToggleSetting
        {
            Name = "onboard_profiles",
            Label = "Onboard Memory Profiles",
            Description = "When enabled the mouse runs its stored onboard profile — needed for button assignments and macros. The DPI panel above writes the profile itself, so it works in both modes.",
            Read = () =>
            {
                byte[]? r = _features.Call(FeatureId.OnboardProfiles, 0x20);
                return (r != null && r.Length >= 1) ? r[0] == 0x01 : (bool?)null;
            },
            // func 0x10: mode 0x01 = onboard, 0x02 = host (software controlled)
            Write = onboard => _features.Call(FeatureId.OnboardProfiles, 0x10, (byte)(onboard ? 0x01 : 0x02)),
        });
    }

    // ---- ADJUSTABLE_DPI 0x2201 -------------------------------------------------
    private void BuildDpi()
    {
        if (!_features.Has(FeatureId.AdjustableDpi))
            return;
        var list = ReadDpiList();
        if (list.Count == 0)
            return;

        _Settings_Add(new ChoiceSetting
        {
            Name = "dpi",
            Label = "Sensitivity (DPI)",
            Description = "Mouse movement sensitivity.",
            Choices = list.Select(v => new Choice(v, v.ToString())).ToList(),
            Read = ReadDpi,
            Write = WriteDpi,
        });
    }

    private List<int> ReadDpiList()
    {
        // func 0x10, params (sensor=0, direction=0, chunkIndex); reply[0] echoes sensor, rest is data.
        var bytes = new List<byte>();
        for (int i = 0; i < 0x100; i++)
        {
            byte[]? reply = _features.Call(FeatureId.AdjustableDpi, 0x10, 0x00, 0x00, (byte)i);
            if (reply == null || reply.Length < 3)
                break;
            bytes.AddRange(reply.Skip(1)); // ignore sensor-index echo
            if (bytes.Count >= 2 && bytes[^1] == 0 && bytes[^2] == 0)
                break;
        }
        var dpis = new List<int>();
        int p = 0;
        while (p + 1 < bytes.Count)
        {
            int val = (bytes[p] << 8) | bytes[p + 1];
            if (val == 0) break;
            if ((val >> 13) == 0b111) // range: step + last
            {
                int step = val & 0x1FFF;
                if (p + 3 >= bytes.Count || dpis.Count == 0) break;
                int last = (bytes[p + 2] << 8) | bytes[p + 3];
                for (int v = dpis[^1] + step; v <= last; v += step)
                    dpis.Add(v);
                p += 4;
            }
            else
            {
                dpis.Add(val);
                p += 2;
            }
        }
        return dpis;
    }

    private int? ReadDpi()
    {
        // func 0x20, param (sensor=0). reply: [sensor, curHi, curLo, defHi, defLo]
        byte[]? r = _features.Call(FeatureId.AdjustableDpi, 0x20, 0x00);
        if (r == null || r.Length < 3) return null;
        int val = (r[1] << 8) | r[2];
        if (val == 0 && r.Length >= 5) val = (r[3] << 8) | r[4];
        return val;
    }

    private void WriteDpi(int dpi)
    {
        // func 0x30, params (sensor=0, dpiHi, dpiLo)
        _features.Call(FeatureId.AdjustableDpi, 0x30, 0x00, (byte)(dpi >> 8), (byte)(dpi & 0xFF));
    }

    // ---- REPORT_RATE 0x8060 ----------------------------------------------------
    private void BuildReportRate()
    {
        if (!_features.Has(FeatureId.ReportRate))
            return;
        byte[]? caps = _features.Call(FeatureId.ReportRate, 0x00);
        if (caps == null || caps.Length < 1)
            return;
        int flags = caps[0];
        var choices = new List<Choice>();
        for (int i = 0; i < 8; i++)
            if (((flags >> i) & 1) != 0)
                choices.Add(new Choice(i + 1, $"{i + 1}ms"));
        if (choices.Count == 0)
            return;

        _Settings_Add(new ChoiceSetting
        {
            Name = "report_rate",
            Label = "Report Rate",
            Description = "Frequency of device movement reports.",
            Choices = choices,
            Read = () =>
            {
                byte[]? r = _features.Call(FeatureId.ReportRate, 0x10);
                return (r != null && r.Length >= 1) ? r[0] : (int?)null;
            },
            Write = v => _features.Call(FeatureId.ReportRate, 0x20, (byte)v),
        });
    }

    private void _Settings_Add(Setting s) => Settings.Add(s);

    public void Dispose() => _transport.Dispose();
}
