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
                byte[]? bytes = d.ReadButtonBytes(b);
                if (bytes == null) return (false, "couldn't read the current button mappings");
                backup[b] = Convert.ToHexString(bytes);
            }
            cfg.HwBackup = backup;
            cfg.Save(); // persist the backup before touching flash
        }

        for (int b = ProfilesConfig.FirstButton; b <= ProfilesConfig.LastButton; b++)
        {
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
    /// Make the mouse match the armed state: any of buttons 4-8 not carrying its
    /// signal key is written back to it (and backed up first if there is no backup
    /// yet). Heals a mapping left behind by a crash mid-write, by G HUB, or by an
    /// older build — without it those buttons never reach the host again.
    /// Returns how many were repaired.
    /// </summary>
    public static int EnsureSignalKeys(IButtonDevice d, ProfilesConfig cfg)
    {
        if (!cfg.Enabled) return 0;
        int repaired = 0;
        for (int b = ProfilesConfig.FirstButton; b <= ProfilesConfig.LastButton; b++)
        {
            byte[]? current;
            try { current = d.ReadButtonBytes(b); } catch { continue; }
            if (current == null || current.AsSpan().SequenceEqual(SignalBytes(b))) continue;
            if (!cfg.HwBackup.ContainsKey(b)) cfg.HwBackup[b] = Convert.ToHexString(current);
            try { if (d.RemapButton(b, SignalBytes(b)).ok) repaired++; } catch { }
        }
        if (repaired > 0) cfg.Save();
        return repaired;
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
