namespace Rodent.Core.Hidpp;

/// <summary>
/// Reads Logitech onboard profiles (feature 0x8100) and decodes each button's
/// current action. This is the persistent, on-device button mapping G HUB writes.
/// READ-ONLY for now — a faithful port of Solaar's OnboardProfiles/Button parsing
/// (lib/logitech_receiver/hidpp20.py). Flash writing is a later phase.
/// </summary>
public static class OnboardProfiles
{
    public sealed record ProfileInfo(int Buttons, int GButtons, int Sectors, int Size);

    public sealed record ButtonAction(int Index, string Label, bool IsMacro = false);

    /// <summary>getInfo (func 0x00). Null if the device has no usable onboard memory.</summary>
    public static ProfileInfo? ReadInfo(FeatureTable f)
    {
        byte[]? r = f.Call(FeatureId.OnboardProfiles, 0x00);
        if (r == null || r.Length < 10) return null;
        int memory = r[0];
        if (memory != 0x01) return null;         // 0x01 = writable onboard memory
        int buttons = r[5];
        int sectors = r[6];
        int size = (r[7] << 8) | r[8];
        int shift = r[9];
        int gbuttons = (shift & 0x3) == 0x2 ? buttons : 0;
        return new ProfileInfo(buttons, gbuttons, sectors, size);
    }

    /// <summary>Read a memory sector (func 0x50), size bytes, mirroring Solaar's chunking.</summary>
    private static byte[]? ReadSector(FeatureTable f, int sector, int size)
    {
        var buf = new List<byte>(size);
        int o = 0;
        while (o < size - 15)
        {
            byte[]? c = f.Call(FeatureId.OnboardProfiles, 0x50,
                (byte)(sector >> 8), (byte)(sector & 0xFF), (byte)(o >> 8), (byte)(o & 0xFF));
            if (c == null || c.Length < 16) return null;
            buf.AddRange(c[..16]);
            o += 16;
        }
        // final (possibly overlapping) chunk to reach exactly `size`
        byte[]? last = f.Call(FeatureId.OnboardProfiles, 0x50,
            (byte)(sector >> 8), (byte)(sector & 0xFF), (byte)((size - 16) >> 8), (byte)((size - 16) & 0xFF));
        if (last == null || last.Length < 16) return null;
        buf.AddRange(last[(16 + o - size)..16]);
        return buf.ToArray();
    }

    /// <summary>Parse the control sector's profile headers -> list of (sector, enabled).</summary>
    private static List<(int sector, bool enabled)> ReadHeaders(FeatureTable f, int size)
    {
        var headers = new List<(int, bool)>();
        byte[]? ctrl = ReadSector(f, 0x0000, size);
        // Empty control sector -> profiles live in ROM (sector 0x0100).
        if (ctrl == null || IsBlank(ctrl))
            ctrl = ReadSector(f, 0x0100, size);
        if (ctrl == null) return headers;

        for (int i = 0; i + 3 < ctrl.Length; i += 4)
        {
            int sector = (ctrl[i] << 8) | ctrl[i + 1];
            if (sector == 0xFFFF || sector == 0x0000) break;
            headers.Add((sector, ctrl[i + 2] != 0));
        }
        return headers;
    }

    private static bool IsBlank(byte[] b) =>
        b.Length >= 4 && ((b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0) ||
                          (b[0] == 0xFF && b[1] == 0xFF && b[2] == 0xFF && b[3] == 0xFF));

    /// <summary>Read and decode the buttons of the first (or active) profile.</summary>
    public static List<ButtonAction> ReadButtons(FeatureTable f)
    {
        var result = new List<ButtonAction>();
        var info = ReadInfo(f);
        if (info == null) return result;

        var headers = ReadHeaders(f, info.Size);
        if (headers.Count == 0) return result;

        // Prefer the active profile if we can read it; else the first header.
        int sector = headers[0].sector;
        byte[]? active = f.Call(FeatureId.OnboardProfiles, 0x40);
        if (active != null && active.Length >= 2)
        {
            int a = (active[0] << 8) | active[1];
            if (headers.Exists(h => h.sector == a)) sector = a;
        }

        byte[]? data = ReadSector(f, sector, info.Size);
        if (data == null) return result;

        byte[]? macroSectorData = null; // lazily loaded to decode macro buttons

        for (int i = 0; i < info.Buttons; i++)
        {
            int off = 32 + i * 4;
            if (off + 4 > data.Length) break;
            byte b0 = data[off], b1 = data[off + 1], b2 = data[off + 2], b3 = data[off + 3];

            string label;
            bool isMacro = (b0 >> 4) <= 0x1 && !(b0 == 0xFF && b1 == 0xFF); // MACRO_EXECUTE/STOP
            if (isMacro)
            {
                int macroSector = ((b0 & 0x0F) << 8) | b1;
                int address = (b2 << 8) | b3;
                macroSectorData ??= ReadSector(f, macroSector, info.Size);
                label = macroSectorData != null ? DecodeMacro(macroSectorData, address) : "Macro";
            }
            else label = DecodeButton(b0, b1, b2, b3);

            result.Add(new ButtonAction(i + 1, label, isMacro));
        }
        return result;
    }

    // ===== WRITE PATH (Phase 2) ================================================

    /// <summary>An action a button can be reassigned to (label + its 4 encoded bytes).</summary>
    public sealed record Assignable(string Label, byte[] Bytes);

    /// <summary>Curated set of safe, common reassignments offered in the UI.</summary>
    public static readonly IReadOnlyList<Assignable> Catalog = new List<Assignable>
    {
        new("Left Click",     new byte[] { 0x80, 0x01, 0x00, 0x01 }),
        new("Right Click",    new byte[] { 0x80, 0x01, 0x00, 0x02 }),
        new("Middle Click",   new byte[] { 0x80, 0x01, 0x00, 0x04 }),
        new("Back",           new byte[] { 0x80, 0x01, 0x00, 0x08 }),
        new("Forward",        new byte[] { 0x80, 0x01, 0x00, 0x10 }),
        new("DPI +",          new byte[] { 0x90, 0x03, 0xFF, 0x00 }),
        new("DPI -",          new byte[] { 0x90, 0x04, 0xFF, 0x00 }),
        new("Cycle DPI",      new byte[] { 0x90, 0x05, 0xFF, 0x00 }),
        new("DPI Shift (sniper)", new byte[] { 0x90, 0x07, 0xFF, 0x00 }),
        new("Next Profile",   new byte[] { 0x90, 0x08, 0xFF, 0x00 }),
        new("Cycle Profile",  new byte[] { 0x90, 0x0A, 0xFF, 0x00 }),
        new("Copy (Ctrl+C)",  new byte[] { 0x80, 0x02, 0x01, 0x06 }),
        new("Paste (Ctrl+V)", new byte[] { 0x80, 0x02, 0x01, 0x19 }),
        new("Unassigned",     new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }),
    };

    /// <summary>Return the raw 4 action bytes of a button (1-based) in the active profile.</summary>
    public static byte[]? ReadButtonBytes(FeatureTable f, int buttonIndex1Based)
    {
        var info = ReadInfo(f);
        if (info == null) return null;
        int? sec = GetActiveSector(f, info);
        if (sec == null) return null;
        byte[]? data = ReadSector(f, sec.Value, info.Size);
        if (data == null) return null;
        int off = 32 + (buttonIndex1Based - 1) * 4;
        return off + 4 <= data.Length ? data[off..(off + 4)] : null;
    }

    /// <summary>CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF) — matches the device.</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
                crc = (ushort)(((crc & 0x8000) != 0) ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }

    private static int? GetActiveSector(FeatureTable f, ProfileInfo info)
    {
        var headers = ReadHeaders(f, info.Size);
        if (headers.Count == 0) return null;
        int sector = headers[0].sector;
        byte[]? active = f.Call(FeatureId.OnboardProfiles, 0x40);
        if (active != null && active.Length >= 2)
        {
            int a = (active[0] << 8) | active[1];
            if (headers.Exists(h => h.sector == a)) sector = a;
        }
        return sector;
    }

    private static bool WriteSector(FeatureTable f, int sector, byte[] bs)
    {
        int len = bs.Length;
        // start write (func 0x60): sector, offset 0, length
        if (f.Call(FeatureId.OnboardProfiles, 0x60,
                (byte)(sector >> 8), (byte)(sector & 0xFF), 0, 0, (byte)(len >> 8), (byte)(len & 0xFF)) == null)
            return false;
        for (int o = 0; o < len; o += 16)
        {
            var chunk = new byte[16];
            Array.Copy(bs, o, chunk, 0, Math.Min(16, len - o));
            if (f.Call(FeatureId.OnboardProfiles, 0x70, chunk) == null)
                return false;
        }
        f.Call(FeatureId.OnboardProfiles, 0x80); // end write (may not reply)
        return true;
    }

    /// <summary>
    /// Reassign button (1-based) to the given 4-byte action, in the active profile.
    /// Reads the sector, patches the button, fixes CRC, writes, and reads back to verify.
    /// Returns true only if the read-back matches. Non-destructive on failure to verify.
    /// </summary>
    public static bool WriteButton(FeatureTable f, int buttonIndex1Based, byte[] action4)
    {
        var info = ReadInfo(f);
        if (info == null) return false;
        int? sec = GetActiveSector(f, info);
        if (sec == null) return false;
        int sector = sec.Value;

        byte[]? data = ReadSector(f, sector, info.Size);
        if (data == null) return false;

        int off = 32 + (buttonIndex1Based - 1) * 4;
        if (off + 4 > data.Length - 2) return false; // don't touch CRC region
        Array.Copy(action4, 0, data, off, 4);

        // recompute CRC over everything but the last 2 bytes (big-endian)
        ushort crc = Crc16(data.AsSpan(0, data.Length - 2));
        data[^2] = (byte)(crc >> 8);
        data[^1] = (byte)(crc & 0xFF);

        if (!WriteSector(f, sector, data)) return false;

        byte[]? verify = ReadSector(f, sector, info.Size);
        if (verify == null || verify.Length != data.Length) return false;
        for (int i = 0; i < data.Length; i++)
            if (verify[i] != data[i]) return false;
        return true;
    }

    /// <summary>Decode a 4-byte action into a human label (for UI after a write).</summary>
    public static string Decode(byte[] b) =>
        b.Length >= 4 ? DecodeButton(b[0], b[1], b[2], b[3]) : "?";

    /// <summary>
    /// Walk a stored macro (from `address` in a sector) and summarize it: a key
    /// combo like "Ctrl+Tab", or "Type: hello" for typed text.
    /// </summary>
    public static string DecodeMacro(byte[] data, int address)
    {
        var presses = new List<(byte mod, byte key)>();
        int delays = 0;
        bool repeats = false;
        int pos = address, guard = 0;
        while (pos >= 0 && pos < data.Length && guard++ < 400)
        {
            byte op = data[pos];
            if (op == 0xFF) break;                                   // END
            if (op == 0x02 || op == 0x03) { repeats = true; pos += 1; continue; } // repeat terminator
            switch (op)
            {
                case 0x43: presses.Add((data[pos + 1], data[pos + 2])); pos += 3; break; // KEY_PRESS
                case 0x40: delays++; pos += 3; break;                                    // DELAY
                case 0x60:                                                               // JUMP
                    int tgt = (data[pos + 2] << 8) | data[pos + 1];
                    pos = (tgt > address && tgt < data.Length) ? tgt : pos + 3;
                    break;
                default:
                    int len = Macro.OpLength(op);
                    if (len == 0) return "Macro";                    // unknown → give up cleanly
                    pos += len;
                    break;
            }
        }

        string suffix = repeats ? " (repeats)" : "";
        if (presses.Count == 0) return (delays > 0 ? "Delay macro" : "Macro") + suffix;

        bool typed = presses.Count >= 2 && presses.TrueForAll(p => Macro.IsPrintable(p.key) && (p.mod & ~Macro.ModShift) == 0);
        if (typed)
        {
            var sb = new System.Text.StringBuilder("Type: ");
            foreach (var (mod, key) in presses) sb.Append(Macro.KeyToChar(key, (mod & Macro.ModShift) != 0));
            string s = sb.ToString();
            if (s.Length > 22) s = s[..22] + "…";
            return s + suffix;
        }
        var names = new List<string>();
        foreach (var (_, key) in presses)
        {
            string n = Macro.KeyName(key);
            if (!names.Contains(n)) names.Add(n);
        }
        return string.Join("+", names) + suffix;
    }

    // ===== MACROS (Phase 3) ====================================================

    /// <summary>4-byte button action pointing at a macro at (sector, address).</summary>
    public static byte[] MacroButtonBytes(int sector, int address) =>
        new byte[] { (byte)((sector >> 8) & 0x0F), (byte)(sector & 0xFF), (byte)(address >> 8), (byte)(address & 0xFF) };

    /// <summary>Read a whole sector (public, for inspection/verification).</summary>
    public static byte[]? DumpSector(FeatureTable f, int sector)
    {
        var info = ReadInfo(f);
        return info == null ? null : ReadSector(f, sector, info.Size);
    }

    /// <summary>Overwrite a whole sector with the given bytes (public, for restore in tests).</summary>
    public static bool WriteRawSector(FeatureTable f, int sector, byte[] bytes) =>
        WriteSector(f, sector, bytes);

    /// <summary>
    /// The sector that stores macros: the one referenced by an existing macro button,
    /// else the first sector that is neither the control sector nor a profile sector.
    /// </summary>
    public static int? FindMacroSector(FeatureTable f)
    {
        var info = ReadInfo(f);
        if (info == null) return null;

        int? active = GetActiveSector(f, info);
        if (active != null)
        {
            byte[]? data = ReadSector(f, active.Value, info.Size);
            if (data != null)
                for (int i = 0; i < info.Buttons; i++)
                {
                    int off = 32 + i * 4;
                    if (off + 4 > data.Length) break;
                    if ((data[off] >> 4) <= 0x1) // MACRO_EXECUTE / MACRO_STOP
                        return ((data[off] & 0x0F) << 8) | data[off + 1];
                }
        }

        var used = new HashSet<int> { 0 };
        foreach (var (s, _) in ReadHeaders(f, info.Size)) used.Add(s);
        for (int s = 1; s < info.Sectors; s++)
            if (!used.Contains(s)) return s;
        return null;
    }

    /// <summary>
    /// Write a macro into the free space of a sector (preserving existing macros),
    /// at a 16-byte-aligned address, fix the sector CRC, verify read-back. When the
    /// sector is full, unreferenced (orphaned) macros are garbage-collected first.
    /// Returns (success, address the macro was written at, failure reason).
    /// </summary>
    public static (bool ok, int? address, string? error) WriteMacroInto(FeatureTable f, int sector, byte[] macroBytes)
    {
        var info = ReadInfo(f);
        if (info == null) return (false, null, "device has no onboard memory");
        byte[]? data = ReadSector(f, sector, info.Size);
        if (data == null) return (false, null, "couldn't read the macro sector");

        int usableEnd = info.Size - 2;            // last 2 bytes are the CRC
        int addr = FreeStart(data, usableEnd);
        if (addr + macroBytes.Length > usableEnd)
        {
            byte[]? compacted = CompactMacroSector(f, info, sector, data);
            if (compacted == null) return (false, null, "onboard macro memory is full");
            data = compacted;
            addr = FreeStart(data, usableEnd);
            if (addr + macroBytes.Length > usableEnd)
                return (false, null, "onboard macro memory is full");
        }

        Array.Copy(macroBytes, 0, data, addr, macroBytes.Length);
        ushort crc = Crc16(data.AsSpan(0, usableEnd));
        data[usableEnd] = (byte)(crc >> 8);
        data[usableEnd + 1] = (byte)(crc & 0xFF);

        if (!WriteSector(f, sector, data)) return (false, addr, "device rejected the write");
        byte[]? v = ReadSector(f, sector, info.Size);
        bool ok = v != null && v.Length == data.Length && v.AsSpan().SequenceEqual(data);
        return (ok, addr, ok ? null : "write verification failed");
    }

    /// <summary>First 16-byte-aligned address after the last used byte.</summary>
    private static int FreeStart(byte[] data, int usableEnd)
    {
        int lastUsed = -1;
        for (int i = 0; i < usableEnd; i++)
            if (data[i] != 0xFF) lastUsed = i;
        return ((lastUsed + 1 + 15) / 16) * 16;
    }

    /// <summary>
    /// Rebuild the macro sector keeping only macros still referenced by a button
    /// (repeated assignments orphan old macros — G HUB leaves the same litter), then
    /// repoint the buttons at the compacted addresses. Conservative: any macro whose
    /// byte length can't be determined aborts the whole compaction. Returns the new
    /// sector image, or null if nothing could be reclaimed safely.
    /// </summary>
    private static byte[]? CompactMacroSector(FeatureTable f, ProfileInfo info, int sector, byte[] data)
    {
        int? profSec = GetActiveSector(f, info);
        if (profSec == null) return null;
        byte[]? prof = ReadSector(f, profSec.Value, info.Size);
        if (prof == null) return null;

        // Buttons (and G-shift buttons) pointing into this macro sector.
        var refs = new List<(int profOff, int addr)>();
        void Scan(int baseOff, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int off = baseOff + i * 4;
                if (off + 4 > prof.Length - 2) break;
                if (prof[off] == 0xFF || (prof[off] >> 4) > 0x1) continue; // not a macro ref
                if ((((prof[off] & 0x0F) << 8) | prof[off + 1]) != sector) continue;
                refs.Add((off, (prof[off + 2] << 8) | prof[off + 3]));
            }
        }
        Scan(32, info.Buttons);
        Scan(96, info.GButtons);

        int usableEnd = info.Size - 2;
        var fresh = new byte[info.Size];
        Array.Fill(fresh, (byte)0xFF);
        var newAddr = new Dictionary<int, int>();
        int cursor = 0;
        foreach (var (_, a) in refs)
        {
            if (newAddr.ContainsKey(a)) continue;
            int len = MacroLength(data, a, usableEnd);
            if (len <= 0) return null;                    // unknown encoding — don't risk it
            if (cursor + len > usableEnd) return null;
            Array.Copy(data, a, fresh, cursor, len);
            newAddr[a] = cursor;
            cursor = ((cursor + len + 15) / 16) * 16;
        }

        ushort crc = Crc16(fresh.AsSpan(0, usableEnd));
        fresh[usableEnd] = (byte)(crc >> 8);
        fresh[usableEnd + 1] = (byte)(crc & 0xFF);
        if (!WriteSector(f, sector, fresh)) return null;
        byte[]? vf = ReadSector(f, sector, info.Size);
        if (vf == null || !vf.AsSpan().SequenceEqual(fresh)) return null;

        bool changed = false;
        foreach (var (off, a) in refs)
        {
            int na = newAddr[a];
            if (na == a) continue;
            prof[off + 2] = (byte)(na >> 8);
            prof[off + 3] = (byte)(na & 0xFF);
            changed = true;
        }
        if (changed)
        {
            ushort pcrc = Crc16(prof.AsSpan(0, prof.Length - 2));
            prof[^2] = (byte)(pcrc >> 8);
            prof[^1] = (byte)(pcrc & 0xFF);
            if (!WriteSector(f, profSec.Value, prof)) return null;
            byte[]? vp = ReadSector(f, profSec.Value, info.Size);
            if (vp == null || !vp.AsSpan().SequenceEqual(prof)) return null;
        }
        return fresh;
    }

    /// <summary>
    /// Byte length of the macro at addr through its END byte, or -1 when it doesn't
    /// terminate cleanly or uses an opcode we can't measure (JUMPs make macros
    /// non-contiguous, so they abort too).
    /// </summary>
    private static int MacroLength(byte[] data, int addr, int usableEnd)
    {
        int pos = addr;
        if (pos < 0 || pos >= usableEnd) return -1;
        int guard = 0;
        while (pos < usableEnd && guard++ < 512)
        {
            byte op = data[pos];
            if (op == 0xFF) return pos + 1 - addr;          // END
            if (op == 0x60) return -1;                      // JUMP → non-contiguous, don't move it
            int len = Macro.OpLength(op);
            if (len == 0) return -1;                        // unknown opcode
            pos += len;
        }
        return -1;
    }

    // ---- Button 4-byte decode (behavior = b0 >> 4) ----------------------------
    private static string DecodeButton(byte b0, byte b1, byte b2, byte b3)
    {
        if (b0 == 0xFF && b1 == 0xFF && b2 == 0xFF && b3 == 0xFF) return "Unassigned";
        int behavior = b0 >> 4;
        switch (behavior)
        {
            case 0x0: // MACRO_EXECUTE
            case 0x1: // MACRO_STOP
                return "Macro";
            case 0x8: // SEND
                return b1 switch
                {
                    0x01 => MouseButton((b2 << 8) | b3),                 // BUTTON
                    0x02 => KeyCombo(b2, b3),                            // MODIFIER_AND_KEY
                    0x03 => ConsumerKey((b2 << 8) | b3),                 // CONSUMER_KEY
                    0x00 => "No action",                                 // NO_ACTION
                    _ => $"Send 0x{b1:X2}",
                };
            case 0x9: // FUNCTION
                return Function(b1);
            default:
                return $"Raw {b0:X2}{b1:X2}{b2:X2}{b3:X2}";
        }
    }

    private static string MouseButton(int mask) => mask switch
    {
        0x0001 => "Left Click",
        0x0002 => "Right Click",
        0x0004 => "Middle Click",
        0x0008 => "Back",
        0x0010 => "Forward",
        0x0020 => "Button 6",
        0x0040 => "Button 7",
        0x0080 => "Button 8",
        _ => $"Mouse Button 0x{mask:X4}",
    };

    private static string Function(int fn) => fn switch
    {
        0x0 => "No action",
        0x1 => "Tilt Left",
        0x2 => "Tilt Right",
        0x3 => "DPI +",
        0x4 => "DPI -",
        0x5 => "Cycle DPI",
        0x6 => "Default DPI",
        0x7 => "DPI Shift",
        0x8 => "Next Profile",
        0x9 => "Previous Profile",
        0xA => "Cycle Profile",
        0xB => "G-Shift",
        0xC => "Battery Status",
        0xD => "Profile Select",
        0xE => "Mode Switch",
        0xF => "Host Switch",
        0x10 => "Scroll Down",
        0x11 => "Scroll Up",
        _ => $"Function 0x{fn:X2}",
    };

    private static string KeyCombo(int modifiers, int keycode)
    {
        var parts = new List<string>();
        if ((modifiers & 0x01) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x02) != 0) parts.Add("Shift");
        if ((modifiers & 0x04) != 0) parts.Add("Alt");
        if ((modifiers & 0x08) != 0) parts.Add("Win");
        parts.Add(KeyName(keycode));
        return string.Join("+", parts);
    }

    private static string KeyName(int k)
    {
        if (k >= 0x04 && k <= 0x1D) return ((char)('A' + (k - 0x04))).ToString();
        if (k >= 0x1E && k <= 0x26) return ((char)('1' + (k - 0x1E))).ToString();
        return k switch
        {
            0x27 => "0",
            0x28 => "Enter",
            0x29 => "Esc",
            0x2A => "Backspace",
            0x2B => "Tab",
            0x2C => "Space",
            0x2D => "-", 0x2E => "=", 0x2F => "[", 0x30 => "]", 0x31 => "\\",
            0x33 => ";", 0x34 => "'", 0x35 => "`", 0x36 => ",", 0x37 => ".", 0x38 => "/",
            0x39 => "CapsLock",
            0x3A => "F1", 0x3B => "F2", 0x3C => "F3", 0x3D => "F4",
            0x3E => "F5", 0x3F => "F6", 0x40 => "F7", 0x41 => "F8",
            0x42 => "F9", 0x43 => "F10", 0x44 => "F11", 0x45 => "F12",
            0x46 => "PrintScreen", 0x47 => "ScrollLock", 0x48 => "Pause",
            0x49 => "Insert", 0x4A => "Home", 0x4B => "PgUp", 0x4C => "Delete",
            0x4D => "End", 0x4E => "PgDn",
            0x4F => "Right", 0x50 => "Left", 0x51 => "Down", 0x52 => "Up",
            >= 0x68 and <= 0x73 => $"F{13 + k - 0x68}",
            _ => $"Key 0x{k:X2}",
        };
    }

    private static string ConsumerKey(int c) => c switch
    {
        0x00B5 => "Next Track",
        0x00B6 => "Previous Track",
        0x00B7 => "Stop",
        0x00CD => "Play/Pause",
        0x00E2 => "Mute",
        0x00E9 => "Volume +",
        0x00EA => "Volume -",
        _ => $"Media 0x{c:X4}",
    };
}
