namespace Rodent.Core.Hidpp;

/// <summary>
/// Non-color LED control (feature 0x1300, libratbag's LED_SW_CONTROL). Probed on
/// the real G402: getCount 0x00, getInfo 0x10 [idx, type, physical, capsHi, capsLo],
/// getState 0x40 [idx, mode, ...], setState 0x50 [idx, 0x00, mode] — the device
/// only accepts modes present in caps, and on the G402 rejects everything except
/// re-asserting the current mode (its lighting is fixed in firmware).
/// </summary>
public static class LedControl
{
    public sealed record Zone(int Index, int Type, int Physical, int Caps, int Mode);

    public static readonly (int bit, string name)[] Modes =
    {
        (0x01, "On"), (0x02, "Off"), (0x04, "Blink"), (0x08, "Travel"),
        (0x10, "Ramp Up"), (0x20, "Ramp Down"), (0x40, "Heartbeat"), (0x80, "Breathing"),
    };

    public static string ModeName(int mode) =>
        Modes.FirstOrDefault(m => m.bit == mode).name ?? $"Mode 0x{mode:X2}";

    public static string ZoneName(Zone z)
    {
        // Type numbering observed: 0x02 = the DPI indicator strip, 0x04 = logo.
        string baseName = z.Type switch
        {
            0x01 => "Side lighting",
            0x02 => "DPI indicator",
            0x03 => "Battery indicator",
            0x04 => "Logo",
            _ => $"LED zone {z.Index}",
        };
        return z.Physical > 1 ? $"{baseName} ({z.Physical} LEDs)" : baseName;
    }

    public static List<Zone> Read(FeatureTable f)
    {
        var zones = new List<Zone>();
        if (!f.Has(FeatureId.LedControl)) return zones;

        byte[]? cnt = f.Call(FeatureId.LedControl, 0x00);
        int n = cnt != null && cnt.Length > 0 ? cnt[0] : 0;
        for (int i = 0; i < Math.Min(n, 8); i++)
        {
            byte[]? info = f.Call(FeatureId.LedControl, 0x10, (byte)i);
            byte[]? state = f.Call(FeatureId.LedControl, 0x40, (byte)i);
            if (info == null || info.Length < 5) continue;
            int caps = (info[3] << 8) | info[4];
            int mode = state != null && state.Length >= 2 ? state[1] : 0;
            zones.Add(new Zone(i, info[1], info[2], caps, mode));
        }
        return zones;
    }

    public const int ModeOn = 0x0001, ModeOff = 0x0002, ModeBreathing = 0x0080;

    // Non-volatile config of the DPI-indicator strip (zone type 0x02), reverse-
    // engineered from a live A/B of G HUB's "DPI lighting always on" checkbox:
    // getNvConfig f0x60 [idx] and the undocumented setter f0x70 [idx, value],
    // both gated behind ENABLE_HIDDEN_FEATURES.
    public const int NvStripAlwaysOn = 0x02, NvStripIndicator = 0x04;

    /// <summary>The strip's NV config value, or -1 when unreadable.</summary>
    public static int GetNvConfig(FeatureTable f, int index)
    {
        f.Call(FeatureId.HiddenFeatures, 0x10, 0x01);
        byte[]? r = f.Call(FeatureId.LedControl, 0x60, (byte)index);
        return r != null && r.Length >= 2 ? r[1] : -1;
    }

    /// <summary>Write the strip's NV config (persists across replug).</summary>
    public static bool SetNvConfig(FeatureTable f, int index, int value)
    {
        f.Call(FeatureId.HiddenFeatures, 0x10, 0x01);
        return f.Call(FeatureId.LedControl, 0x70, (byte)index, (byte)value) != null;
    }

    /// <summary>
    /// Flash the DPI strip once (shows the active DPI level). The firmware only
    /// blinks the indicator for its own DPI button — a software DPI change raises
    /// no event — so we emulate it by pulsing the strip's NV config to always-on
    /// and back. No-op unless the firmware owns the LEDs and the strip is in
    /// indicator-only mode (always-on is already lit).
    /// </summary>
    public static void BlinkStrip(FeatureTable f, int ms = 1200)
    {
        byte[]? sw = f.Call(FeatureId.LedControl, 0x20);
        if (sw == null || sw.Length < 1 || sw[0] != 0x00) return; // software owns LEDs: strip is dark anyway
        var strip = Read(f).FirstOrDefault(z => z.Type == 0x02);
        if (strip == null) return;
        if (GetNvConfig(f, strip.Index) != NvStripIndicator) return;
        SetNvConfig(f, strip.Index, NvStripAlwaysOn);
        Thread.Sleep(ms);
        SetNvConfig(f, strip.Index, NvStripIndicator);
    }

    /// <summary>
    /// setState (func 0x50). Payload is [index, mode(u16 BE), brightness(u16 BE),
    /// period(u16 BE ms)] — hardware-verified on the G402: without the brightness
    /// field the LED is driven at 0 and only ghost-glows. period 0 holds steady.
    /// </summary>
    public static bool SetState(FeatureTable f, int index, int mode, int brightness = 0, int periodMs = 0) =>
        f.Call(FeatureId.LedControl, 0x50,
            (byte)index, (byte)(mode >> 8), (byte)mode,
            (byte)(brightness >> 8), (byte)brightness,
            (byte)(periodMs >> 8), (byte)periodMs) != null;

    /// <summary>
    /// Hand the LEDs to software (true) or back to the firmware (false). Re-asserting
    /// the mode resets the LEDs to zero, so this is a no-op when already there —
    /// otherwise every brightness change would restart its fade from black.
    /// </summary>
    public static void SetSoftwareControl(FeatureTable f, bool on)
    {
        byte[]? cur = f.Call(FeatureId.LedControl, 0x20);
        if (cur != null && cur.Length >= 1 && (cur[0] == 0x01) == on) return;
        f.Call(FeatureId.LedControl, 0x30, (byte)(on ? 0x01 : 0x00));
    }

    /// <summary>
    /// Drive every zone for one user-chosen effect, honouring each zone's caps:
    /// the logo does breathing-with-brightness (so Fixed = period 0, Off =
    /// brightness 0), while the DPI strip is On-only hardware — it has no Off
    /// command at all, so darkening it means re-entering software control.
    /// </summary>
    public static void Apply(FeatureTable f, int mode, int brightness, int periodMs, bool firmwareMode,
                             bool stripAlwaysOn = false)
    {
        if (firmwareMode)
        {
            // Hand everything back to the mouse: the stripes light as its DPI-level
            // indicator. The stripes cannot be lit any other way — under software
            // control they accept On but report brightness 0 and stay dark, and Off
            // is rejected outright.
            // The firmware seeds the logo from the LAST software-driven state at
            // takeover (user-verified: Off → stays dark, Fixed → relights at the
            // fixed value, Breathing → resumes breathing) — the stored profile
            // entries alone don't stop it. So drive the logo dark first, give the
            // fade time to reach zero, then hand over.
            byte[]? sw = f.Call(FeatureId.LedControl, 0x20);
            if (sw != null && sw.Length >= 1 && sw[0] == 0x01) // only drivable under sw control
            {
                foreach (var z in Read(f))
                    if ((z.Caps & ModeBreathing) != 0)
                        SetState(f, z.Index, ModeBreathing, 0, 0);
                Thread.Sleep(300); // brightness fades gradually; land at 0 before takeover
            }
            SetSoftwareControl(f, false);
            foreach (var z in Read(f))
                if (z.Type == 0x02) // the DPI-indicator strip
                {
                    // NV config is flash — skip the write when it already matches
                    // (profile switching re-applies lighting on every selection).
                    int want = stripAlwaysOn ? NvStripAlwaysOn : NvStripIndicator;
                    if (GetNvConfig(f, z.Index) != want) SetNvConfig(f, z.Index, want);
                }
            return;
        }
        SetSoftwareControl(f, true);
        foreach (var z in Read(f))
            if ((z.Caps & ModeBreathing) != 0)
                SetState(f, z.Index, ModeBreathing,
                         mode == 0 ? 0 : brightness, mode == ModeBreathing ? periodMs : 0);
    }
}
