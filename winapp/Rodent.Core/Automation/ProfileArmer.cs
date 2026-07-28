using Rodent.Core.Devices;

namespace Rodent.Core.Automation;

/// <summary>
/// Arms/disarms the per-app profile system on the hardware side: backs up the
/// onboard actions of buttons 4-8, remaps them to signal keys F13-F17 (keys no
/// real keyboard emits), and forces onboard mode so the mouse actually sends
/// them. The host keyboard hook then owns those buttons entirely. Disarm writes
/// the backup back — the mouse leaves exactly as it arrived.
///
/// Works with any <see cref="IButtonDevice"/>; <see cref="SignalBytes"/> is the
/// HID++ encoding, so a brand whose actions encode differently supplies its own
/// (see <see cref="IButtonDevice.RemapButton"/>).
/// </summary>
public static class ProfileArmer
{
    /// <summary>HID usage the onboard mapping emits for a button (F13.. = 0x68..).</summary>
    public static byte SignalHid(int button) => (byte)(0x68 + (button - ProfilesConfig.FirstButton));

    /// <summary>Onboard 4-byte action: SEND MODIFIER_AND_KEY, no mods, F13+.</summary>
    public static byte[] SignalBytes(int button) => new byte[] { 0x80, 0x02, 0x00, SignalHid(button) };

    public static (bool ok, string? error) Arm(IButtonDevice d, ProfilesConfig cfg)
    {
        // Backup once (kept across sessions until a clean restore).
        if (cfg.HwBackup.Count == 0)
        {
            var backup = new Dictionary<int, string>();
            for (int b = ProfilesConfig.FirstButton; b <= ProfilesConfig.LastButton; b++)
            {
                if (cfg.IsHardware(b)) continue;          // kept on the mouse: not ours to touch
                byte[]? bytes = d.ReadButtonBytes(b);
                if (bytes == null) return (false, "couldn't read the current button mappings");
                backup[b] = Convert.ToHexString(bytes);
            }
            cfg.HwBackup = backup;
            cfg.Save(); // persist the backup before touching flash
        }

        for (int b = ProfilesConfig.FirstButton; b <= ProfilesConfig.LastButton; b++)
        {
            if (cfg.IsHardware(b)) continue;
            var (ok, _) = d.RemapButton(b, SignalBytes(b));
            if (!ok)
            {
                TryRestore(d, cfg);
                return (false, $"couldn't remap button {b} — mouse left restored");
            }
        }
        if (!d.EnableOnboardMode())
        {
            TryRestore(d, cfg);
            return (false, "couldn't enable onboard mode");
        }
        cfg.Enabled = true;
        cfg.Save();
        return (true, null);
    }

    public static (bool ok, string? error) Disarm(IButtonDevice d, ProfilesConfig cfg)
    {
        bool all = TryRestore(d, cfg);
        if (all) cfg.HwBackup.Clear(); // next arm re-reads, so onboard edits survive
        cfg.Enabled = false;
        cfg.Save();
        return all ? (true, null) : (false, "some buttons couldn't be restored — try again");
    }

    /// <summary>
    /// Keep a button on the mouse: write the action into flash and take the button
    /// out of the armed set, so arming never overwrites it and disarming never
    /// restores over it. This is what makes a key work in a game that ignores
    /// injected input — the mouse itself sends it.
    /// </summary>
    public static (bool ok, string? error) KeepOnMouse(IButtonDevice d, ProfilesConfig cfg, int button, byte[] action)
    {
        var (ok, _) = d.RemapButton(button, action);
        if (!ok) return (false, "couldn't write to the mouse. If G HUB is running, quit it and try again");
        if (!cfg.IsHardware(button)) cfg.HardwareButtons.Add(button);
        cfg.HwBackup.Remove(button);                     // its old action is gone for good
        cfg.Save();
        try { if (!d.IsOnboardMode()) d.EnableOnboardMode(); } catch { }
        return (true, null);
    }

    /// <summary>Hand a kept button back to the per-app engine (remap to its signal key).</summary>
    public static (bool ok, string? error) GiveBackToProfiles(IButtonDevice d, ProfilesConfig cfg, int button)
    {
        cfg.HardwareButtons.Remove(button);
        if (cfg.Enabled)
        {
            byte[]? bytes = d.ReadButtonBytes(button);
            if (bytes != null) cfg.HwBackup[button] = Convert.ToHexString(bytes);
            var (ok, _) = d.RemapButton(button, SignalBytes(button));
            if (!ok)
            {
                cfg.HardwareButtons.Add(button);
                return (false, "couldn't remap the button — it stays on the mouse");
            }
        }
        cfg.Save();
        return (true, null);
    }

    private static bool TryRestore(IButtonDevice d, ProfilesConfig cfg)
    {
        bool all = true;
        foreach (var (b, hex) in cfg.HwBackup)
        {
            try { all &= d.RemapButton(b, Convert.FromHexString(hex)).ok; }
            catch { all = false; }
        }
        return all;
    }
}
