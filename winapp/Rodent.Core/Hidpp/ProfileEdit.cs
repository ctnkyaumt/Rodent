namespace Rodent.Core.Hidpp;

/// <summary>
/// Lighting + DPI settings stored inside the onboard profile sector — the way
/// G HUB actually controls the G402 (feature 0x1300 reads are stale/ignored).
/// Layout per libratbag hidpp20.h struct hidpp20_internal_led (11 bytes):
///   [mode, ...effect union...]; modes: 0x00 Off, 0x01 Fixed{rgb,effect},
///   0x0A Breathing{rgb, period u16 BE ms, waveform, intensity(0=100%)}.
/// G402 profile offsets (verified on hardware dump): leds[2] at 0x0D0/0x0DB,
/// g-shift alt_leds[2] at 0x0E6/0x0F1. DPI block: byte0 report-rate(ms),
/// byte1 default slot index, byte2 shift slot index, bytes3.. dpi[5] u16 LE.
/// </summary>
public static class ProfileEdit
{
    public const int LedOff = 0x00, LedFixed = 0x01, LedBreathing = 0x0A;

    // The LED entry offsets inside the profile sector are device-specific (not
    // discoverable over HID++). Callers pass their model's offsets; this G402
    // default keeps the parameter optional for existing call sites.
    // See Rodent.Core.Devices.OnboardLayout.
    private static readonly int[] G402LedOffsets = { 0x0D0, 0x0DB, 0x0E6, 0x0F1 };

    /// <summary>FirmwareMode hands both LEDs back to the mouse (DPI stripes lit,
    /// logo on factory breathing); Mode/colour/period are then unused. StripAlwaysOn
    /// keeps the DPI stripes lit constantly instead of only blinking on DPI change.</summary>
    public sealed record LightingConfig(int Mode, byte R, byte G, byte B, int PeriodMs,
                                        bool FirmwareMode = false, bool StripAlwaysOn = false);
    public sealed record DpiConfig(int ReportRateMs, int DefaultIndex, int ShiftIndex, int[] Slots);

    // ---- shared sector RMW --------------------------------------------------

    private static (byte[]? data, int sector, int size) ReadActive(FeatureTable f)
    {
        var info = OnboardProfiles.ReadInfo(f);
        if (info == null) return (null, 0, 0);
        // Single-profile devices keep it in sector 1.
        byte[]? data = OnboardProfiles.DumpSector(f, 0x0001);
        return (data, 0x0001, info.Size);
    }

    private static bool WriteBack(FeatureTable f, int sector, byte[] data, bool reactivate = true)
    {
        ushort crc = OnboardProfiles.Crc16(data.AsSpan(0, data.Length - 2));
        data[^2] = (byte)(crc >> 8);
        data[^1] = (byte)(crc & 0xFF);
        if (!OnboardProfiles.WriteRawSector(f, sector, data)) return false;
        byte[]? v = OnboardProfiles.DumpSector(f, sector);
        if (v == null || !v.AsSpan().SequenceEqual(data)) return false;

        // Make the device reload the profile so the change takes effect now.
        // Lighting skips this: it is driven live over 0x1300 and forcing onboard
        // mode there just blanks the LEDs.
        if (reactivate)
        {
            f.Call(FeatureId.OnboardProfiles, 0x10, 0x01);                     // onboard mode
            f.Call(FeatureId.OnboardProfiles, 0x30, (byte)(sector >> 8), (byte)(sector & 0xFF));
        }
        return true;
    }

    // ---- lighting -----------------------------------------------------------

    public static LightingConfig? ReadLighting(FeatureTable f, int[]? ledOffsets = null)
    {
        ledOffsets ??= G402LedOffsets;
        var (data, _, _) = ReadActive(f);
        if (data == null || data.Length < 0x100) return null;

        // Current strip behaviour comes from its NV config, firmware/software
        // ownership from swCtrl — both live device state, not profile bytes.
        bool stripAlways = false, firmware = false;
        try
        {
            var strip = LedControl.Read(f).FirstOrDefault(z => z.Type == 0x02);
            if (strip != null)
                stripAlways = LedControl.GetNvConfig(f, strip.Index) == LedControl.NvStripAlwaysOn;
            byte[]? sw = f.Call(FeatureId.LedControl, 0x20);
            firmware = sw != null && sw.Length >= 1 && sw[0] == 0x00;
        }
        catch { }

        int o = ledOffsets[0];
        int mode = data[o];
        return mode switch
        {
            LedFixed => new LightingConfig(mode, data[o + 1], data[o + 2], data[o + 3], 0, firmware, stripAlways),
            LedBreathing => new LightingConfig(mode, data[o + 1], data[o + 2], data[o + 3],
                                               (data[o + 4] << 8) | data[o + 5], firmware, stripAlways),
            _ => new LightingConfig(LedOff, 0, 0, 0, 0, firmware, stripAlways),
        };
    }

    /// <summary>
    /// Write the same effect to all LED entries (logo + DPI strip + g-shift).
    /// Verified against a live G HUB capture: the LEDs follow the profile entries
    /// only while 0x1300 software-control is ON (swCtrl=1) — setting it is also the
    /// "reload" kick after a sector write. G HUB's "Off" is swCtrl=1 + mode 0x00.
    /// (swCtrl=0 = hardware mode: logo off, DPI strip lit as a DPI indicator.)
    /// The strip can't breathe (ON/OFF hardware), so it goes dark during effects —
    /// same behavior as G HUB.
    /// </summary>
    public static bool WriteLighting(FeatureTable f, LightingConfig cfg, bool persist = true, int[]? ledOffsets = null)
    {
        ledOffsets ??= G402LedOffsets;
        if (cfg.FirmwareMode)
        {
            // The firmware re-reads the stored LED entries the moment it takes the
            // LEDs back (user-verified: Off→firmware kept the logo dark, while
            // Breathing→firmware relit it breathing). So DPI-stripes mode persists
            // "Off" entries — stripes work as the DPI indicator, logo stays dark.
            // Sector write goes FIRST: it blanks the LEDs, and the firmware must
            // find the new entries when control is handed over. This happens even
            // for live per-app switches (ignoring `persist`): stale Fixed/Breathing
            // entries would relight the logo on every switch into this mode, and
            // custom effects never persist on per-app switches, so after the first
            // write the entries stay Off and the unchanged-skip guard makes every
            // later switch flash-free.
            WriteLedEntries(f, new LightingConfig(LedOff, 0, 0, 0, 0), ledOffsets);
            LedControl.Apply(f, 0, 0, 0, firmwareMode: true, cfg.StripAlwaysOn);
            return true;
        }

        // Custom effects: drive the LEDs live FIRST — a flash write blanks them, so
        // persisting first makes every change fade up from black instead of moving
        // smoothly from the current level. Per-app switches skip the flash write.
        int brightness = Math.Max(cfg.G, cfg.B);
        int period = cfg.Mode == LedBreathing ? cfg.PeriodMs : 0;  // Fixed = steady
        LedControl.Apply(f, cfg.Mode == LedOff ? 0 : LedControl.ModeBreathing,
                         brightness, period, firmwareMode: false, cfg.StripAlwaysOn);
        if (persist) WriteLedEntries(f, cfg, ledOffsets); // for replug (the copy G HUB stores)
        return true;
    }

    /// <summary>Write cfg into every profile LED entry (logo + strip + g-shift).
    /// Never reactivates the profile — that forces onboard mode and blanks the LEDs.</summary>
    private static bool WriteLedEntries(FeatureTable f, LightingConfig cfg, int[] ledOffsets)
    {
        var (data, sector, _) = ReadActive(f);
        if (data == null || data.Length < 0x100) return false;
        byte[] before = (byte[])data.Clone();

        foreach (int o in ledOffsets)
        {
            Array.Clear(data, o, 11);
            data[o] = (byte)cfg.Mode;
            if (cfg.Mode == LedFixed)
            {
                data[o + 1] = cfg.R; data[o + 2] = cfg.G; data[o + 3] = cfg.B;
                data[o + 4] = 0x00; // effect: none
            }
            else if (cfg.Mode == LedBreathing)
            {
                data[o + 1] = cfg.R; data[o + 2] = cfg.G; data[o + 3] = cfg.B;
                data[o + 4] = (byte)(cfg.PeriodMs >> 8);
                data[o + 5] = (byte)(cfg.PeriodMs & 0xFF);
                data[o + 6] = 0x00; // waveform: default
                data[o + 7] = 0x00; // intensity: 0 = 100%
            }
        }
        if (data.AsSpan().SequenceEqual(before)) return true; // no change — spare the flash
        return WriteBack(f, sector, data, reactivate: false);
    }

    // ---- DPI ---------------------------------------------------------------

    public static DpiConfig? ReadDpi(FeatureTable f)
    {
        var (data, _, _) = ReadActive(f);
        if (data == null || data.Length < 16) return null;
        var slots = new int[5];
        for (int i = 0; i < 5; i++)
            slots[i] = data[3 + i * 2] | (data[4 + i * 2] << 8); // LE
        return new DpiConfig(data[0], data[1], data[2], slots);
    }

    public static bool WriteDpi(FeatureTable f, DpiConfig cfg)
    {
        var (data, sector, _) = ReadActive(f);
        if (data == null || data.Length < 16) return false;

        data[0] = (byte)cfg.ReportRateMs;
        data[1] = (byte)cfg.DefaultIndex;
        data[2] = (byte)cfg.ShiftIndex;
        for (int i = 0; i < 5; i++)
        {
            int v = i < cfg.Slots.Length ? cfg.Slots[i] : 0;
            data[3 + i * 2] = (byte)(v & 0xFF);
            data[4 + i * 2] = (byte)(v >> 8);
        }
        if (!WriteBack(f, sector, data)) return false;

        // Also apply live (covers host mode): current DPI + report rate.
        int cur = cfg.Slots[Math.Clamp(cfg.DefaultIndex, 0, 4)];
        if (cur > 0)
            f.Call(FeatureId.AdjustableDpi, 0x30, 0x00, (byte)(cur >> 8), (byte)(cur & 0xFF));
        f.Call(FeatureId.ReportRate, 0x20, (byte)cfg.ReportRateMs);
        try { LedControl.BlinkStrip(f); } catch { /* feedback only */ }
        return true;
    }
}
