using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Rodent.Core.Automation;
using Rodent.Core.Devices;
using Rodent.Core.Hidpp;
using Rodent.Core.Model;

// ---- Phase 4 automation smoke test: foreground detect + text injection ----
if (args.Contains("automation"))
{
    Console.WriteLine("Launching Notepad to test foreground detection + injection...");
    var np = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })!;
    Thread.Sleep(1400);
    np.Refresh();
    IntPtr h = np.MainWindowHandle;

    var watcher = new ForegroundWatcher();
    watcher.Start();
    Thread.Sleep(200);
    Native.SetForegroundWindow(h);
    Thread.Sleep(500);
    Console.WriteLine($"foreground app detected: '{watcher.CurrentApp}'");

    InputInjector.TypeText("hello from rodent");
    Thread.Sleep(400);

    IntPtr edit = Native.FindWindowEx(h, IntPtr.Zero, "Edit", null);
    var sb = new StringBuilder(256);
    Native.SendMessage(edit, 0x000D /*WM_GETTEXT*/, (IntPtr)256, sb);
    Console.WriteLine($"after TypeText:  '{sb}'");

    // Key chord test: Ctrl+A (select all) then type over it.
    InputInjector.KeyChord(0x41 /*A*/, InputInjector.VK_CONTROL);
    Thread.Sleep(300);
    InputInjector.TypeText("chord works");
    Thread.Sleep(400);
    sb.Clear();
    Native.SendMessage(edit, 0x000D, (IntPtr)256, sb);
    Console.WriteLine($"after Ctrl+A + type: '{sb}'  (expect only 'chord works')");

    watcher.Stop();
    try { np.Kill(); } catch { }
    return;
}

if (args.Contains("toggletest"))
{
    var cfg = new ProfilesConfig { Enabled = true };
    var w = cfg.Wildcard();
    w.Buttons[4] = new ButtonBinding { Kind = BindingKind.RepeatText, Text = "test " };
    using var svc = new AutomationService(cfg);
    svc.Start();

    var np = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true })!;
    Thread.Sleep(1400); np.Refresh();
    IntPtr h = np.MainWindowHandle;
    IntPtr edit = Native.FindWindowEx(h, IntPtr.Zero, "Edit", null);
    int Len() { var sb = new StringBuilder(4096); Native.SendMessage(edit, 0x000D, (IntPtr)4096, sb); return sb.ToString().Length; }

    void PressSignal() { Native.SetForegroundWindow(h); Thread.Sleep(200); Native.keybd_event(0x7C, 0, 0, UIntPtr.Zero); Thread.Sleep(40); Native.keybd_event(0x7C, 0, 2, UIntPtr.Zero); }

    Native.SetForegroundWindow(h); Thread.Sleep(300);
    PressSignal();                                  // start
    Thread.Sleep(1500);
    int a = Len();
    Console.WriteLine($"   after 1.5s running: {a} chars, RepeatActive={svc.RepeatActive}  (want >0, True)");
    PressSignal();                                  // toggle off
    Thread.Sleep(400);
    int b = Len(); Thread.Sleep(1200); int c = Len();
    Console.WriteLine($"   after stop: {b} -> {c} chars, RepeatActive={svc.RepeatActive}  (want unchanged, False)");

    // Esc panic stop
    PressSignal(); Thread.Sleep(1000);
    Native.SetForegroundWindow(h); Thread.Sleep(150);
    Native.keybd_event(0x1B, 0, 0, UIntPtr.Zero); Thread.Sleep(40); Native.keybd_event(0x1B, 0, 2, UIntPtr.Zero);
    Thread.Sleep(500);
    Console.WriteLine($"   after Esc: RepeatActive={svc.RepeatActive}  (want False)");

    svc.Stop();
    try { np.Kill(); } catch { }
    return;
}

if (args.Contains("macroencode"))
{
    // Mimic what the editor builds: type "gg", 100ms delay, then Ctrl+C.
    var steps = new List<Macro.Step>();
    foreach (char c in "gg") { var (k, sh) = Macro.CharToKey(c); steps.Add(new(Macro.Kind.KeyDown, sh ? Macro.ModShift : (byte)0, k)); steps.Add(new(Macro.Kind.KeyUp, sh ? Macro.ModShift : (byte)0, k)); }
    steps.Add(new(Macro.Kind.Delay, DelayMs: 100));
    steps.Add(new(Macro.Kind.KeyDown, Macro.ModCtrl, 0x06)); // Ctrl+C (c = 0x06)
    steps.Add(new(Macro.Kind.KeyUp, Macro.ModCtrl, 0x06));
    Console.WriteLine("Once:      " + Hex(Macro.Encode(steps, Macro.RepeatMode.Once)));
    Console.WriteLine("WhileHeld: " + Hex(Macro.Encode(steps, Macro.RepeatMode.WhileHeld)));
    Console.WriteLine("Toggle:    " + Hex(Macro.Encode(steps, Macro.RepeatMode.Toggle)));
    return;
}

var devices = DeviceManager.Discover();
Console.WriteLine($"Found {devices.Count} device(s).\n");
foreach (var d in devices)
{
    Console.WriteLine($"== {d.Name}  ({d.Kind}, {d.VendorId:X4}:{d.ProductId:X4})");
    foreach (var info in d.Info) Console.WriteLine($"   - {info.Label}: {info.Value}");
    foreach (var s in d.Settings)
        if (s is ChoiceSetting cs)
            Console.WriteLine($"   [{s.Name}] {s.Label} ({cs.Choices.Count} choices)");
        else if (s is ToggleSetting) Console.WriteLine($"   [{s.Name}] {s.Label} (toggle)");
    foreach (var b in d.Buttons) Console.WriteLine($"   button {b.Index}: {b.Label}");

    if (args.Contains("writetest") && d.Buttons.Count >= 7)
    {
        const int idx = 7; // Win+Tab on the G402
        byte[]? orig = OnboardProfiles.ReadButtonBytes(d.Features, idx);
        Console.WriteLine($"\n   WRITE TEST on button {idx} (orig bytes: {Hex(orig)})");

        var (ok, label) = d.RemapButton(idx, new byte[] { 0x80, 0x01, 0x00, 0x04 }); // Middle Click
        Console.WriteLine($"   wrote Middle Click -> ok={ok}, reads back: {label}");

        if (orig != null)
        {
            var (ok2, label2) = d.RemapButton(idx, orig);
            Console.WriteLine($"   restored original -> ok={ok2}, reads back: {label2}");
        }
    }

    if (args.Contains("mode"))
    {
        byte[]? m = d.Features.Call(FeatureId.OnboardProfiles, 0x20);
        byte[]? act = d.Features.Call(FeatureId.OnboardProfiles, 0x40);
        Console.WriteLine($"\n   onboard mode: {(m != null && m.Length > 0 ? m[0].ToString() : "?")} (1=onboard, 2=host)");
        Console.WriteLine($"   active profile: {(act != null && act.Length >= 2 ? $"0x{(act[0] << 8) | act[1]:X4}" : "?")}");
        // LED / lighting feature probe for the Lighting tab
        foreach (var (id, name) in new (ushort, string)[] { (0x8070, "COLOR_LED_EFFECTS"), (0x8071, "RGB_EFFECTS"), (0x1300, "LED_CONTROL"), (0x8080, "PER_KEY_LIGHTING"), (0x1990, "ILLUMINATION") })
            Console.WriteLine($"   feature 0x{id:X4} {name}: {(d.Features.Has(id) ? $"index 0x{d.Features.GetIndex(id):X2}" : "absent")}");
    }

    if (args.Contains("leds"))
    {
        // LED_CONTROL 0x1300 read-only exploration (libratbag LED_SW_CONTROL layout).
        byte[]? cnt = d.Features.Call(0x1300, 0x00);
        Console.WriteLine($"\n   leds getCount: {Hex(cnt)}");
        int n = cnt != null && cnt.Length > 0 ? cnt[0] : 0;
        for (int i = 0; i < Math.Min(n, 4); i++)
        {
            Console.WriteLine($"   led {i} info  (0x10): {Hex(d.Features.Call(0x1300, 0x10, (byte)i))}");
            Console.WriteLine($"   led {i} state (0x40): {Hex(d.Features.Call(0x1300, 0x40, (byte)i))}");
        }
        Console.WriteLine($"   swCtrl (0x20): {Hex(d.Features.Call(0x1300, 0x20))}");
    }

    if (args.Contains("ledwrite"))
    {
        // Reversible experiment: LED0 (DPI dots) ON -> OFF -> ON, LE mode order first.
        HidppTransport.Debug = true;
        Console.WriteLine($"\n   before:      {Hex(d.Features.Call(0x1300, 0x40, 0x00))}");
        var r1 = d.Features.Call(0x1300, 0x50, 0x00, 0x02, 0x00);   // OFF, mode LE
        Console.WriteLine($"   set OFF(le): {(r1 == null ? "ERROR" : Hex(r1))}");
        Console.WriteLine($"   after:       {Hex(d.Features.Call(0x1300, 0x40, 0x00))}");
        if (r1 == null)
        {
            var r2 = d.Features.Call(0x1300, 0x50, 0x00, 0x00, 0x02); // OFF, mode BE
            Console.WriteLine($"   set OFF(be): {(r2 == null ? "ERROR" : Hex(r2))}");
            Console.WriteLine($"   after:       {Hex(d.Features.Call(0x1300, 0x40, 0x00))}");
        }
        Thread.Sleep(1500);
        var r3 = d.Features.Call(0x1300, 0x50, 0x00, 0x01, 0x00);   // restore ON (le)
        if (r3 == null) d.Features.Call(0x1300, 0x50, 0x00, 0x00, 0x01);
        Console.WriteLine($"   restored:    {Hex(d.Features.Call(0x1300, 0x40, 0x00))}");
    }

    if (args.Contains("ledwrite2"))
    {
        HidppTransport.Debug = true;
        void St(string tag) => Console.WriteLine($"   {tag}: {Hex(d.Features.Call(0x1300, 0x40, 0x01))}");
        St("logo before");
        Console.WriteLine($"   set BREATHING: {(d.Features.Call(0x1300, 0x50, 0x01, 0x00, 0x80) == null ? "ERROR" : "ok")}");
        St("after");
        Console.WriteLine($"   set ON:        {(d.Features.Call(0x1300, 0x50, 0x01, 0x00, 0x01) == null ? "ERROR" : "ok")}");
        St("after");
        Thread.Sleep(1200);
        Console.WriteLine($"   set OFF:       {(d.Features.Call(0x1300, 0x50, 0x01, 0x00, 0x02) == null ? "ERROR" : "ok")}");
        St("after");
        Thread.Sleep(1200);
        Console.WriteLine($"   restore BREATHING: {(d.Features.Call(0x1300, 0x50, 0x01, 0x00, 0x80) == null ? "ERROR" : "ok")}");
        St("final");
    }

    if (args.Contains("ledwrite3"))
    {
        void St2(string tag) => Console.WriteLine($"   {tag}: {Hex(d.Features.Call(0x1300, 0x40, 0x01))}");
        St2("before");
        Console.WriteLine($"   breathing 1000ms/100: {(d.Features.Call(0x1300, 0x50, 0x01, 0x00, 0x80, 0x03, 0xE8, 0x64) == null ? "ERROR" : "ok")}");
        St2("after");
        Thread.Sleep(2500);
        Console.WriteLine($"   breathing 3000ms/40:  {(d.Features.Call(0x1300, 0x50, 0x01, 0x00, 0x80, 0x0B, 0xB8, 0x28) == null ? "ERROR" : "ok")}");
        St2("after");
        Thread.Sleep(2500);
        Console.WriteLine($"   restore default:      {(d.Features.Call(0x1300, 0x50, 0x01, 0x00, 0x80) == null ? "ERROR" : "ok")}");
        St2("final");
    }

    if (args.Contains("dumpprofile"))
    {
        // Full hex of the active profile sector — hunting for the LED config G HUB writes.
        var pinfo = OnboardProfiles.ReadInfo(d.Features);
        byte[]? sec = pinfo == null ? null : OnboardProfiles.DumpSector(d.Features, 0x0001);
        if (sec != null)
            for (int row = 0; row < sec.Length; row += 16)
            {
                var slice = sec[row..Math.Min(row + 16, sec.Length)];
                if (Array.TrueForAll(slice, x => x == 0xFF)) continue;
                Console.WriteLine($"   0x{row:X3}: {string.Join(" ", slice.Select(x => x.ToString("X2")))}");
            }
        Console.WriteLine($"   led0 state: {Hex(d.Features.Call(0x1300, 0x40, 0x00))}");
        Console.WriteLine($"   led1 state: {Hex(d.Features.Call(0x1300, 0x40, 0x01))}");
        Console.WriteLine($"   swCtrl:     {Hex(d.Features.Call(0x1300, 0x20))}");
        Console.WriteLine($"   mode:       {Hex(d.Features.Call(FeatureId.OnboardProfiles, 0x20))}");
    }

    if (args.Contains("ledsw"))
    {
        Console.WriteLine($"   swCtrl before: {Hex(d.Features.Call(0x1300, 0x20))}");
        Console.WriteLine($"   set swCtrl=0:  {(d.Features.Call(0x1300, 0x30, 0x00) == null ? "ERROR" : "ok")}");
        Thread.Sleep(2000);
        Console.WriteLine($"   swCtrl after:  {Hex(d.Features.Call(0x1300, 0x20))}");
        Console.WriteLine($"   led0 state:    {Hex(d.Features.Call(0x1300, 0x40, 0x00))}");
        Console.WriteLine($"   led1 state:    {Hex(d.Features.Call(0x1300, 0x40, 0x01))}");
    }

    if (args.Contains("ledfix"))
    {
        // Full-white breathing into the profile (R byte too) + re-assert 0x1300.
        bool ok = ProfileEdit.WriteLighting(d.Features,
            new ProfileEdit.LightingConfig(ProfileEdit.LedBreathing, 0xFF, 0xFF, 0xFF, 3000));
        Console.WriteLine($"   profile write: {(ok ? "ok" : "FAILED")}");
        Console.WriteLine($"   swCtrl:       {Hex(d.Features.Call(0x1300, 0x20))}");
        Console.WriteLine($"   reassert brth: {(d.Features.Call(0x1300, 0x50, 0x01, 0x00, 0x80) == null ? "rejected" : "ok")}");
        Console.WriteLine($"   led1 state:    {Hex(d.Features.Call(0x1300, 0x40, 0x01))}");
    }

    if (args.Contains("ledghub"))
    {
        // Byte-for-byte replay of the captured working G HUB state:
        // profile LED entries = breathing #00FFFF 3000ms, swCtrl=1, HOST mode.
        byte[] entry = { 0x0A, 0x00, 0xFF, 0xFF, 0x0B, 0xB8, 0x00, 0x00, 0x00, 0x00, 0x00 };
        byte[]? sec = OnboardProfiles.DumpSector(d.Features, 0x0001);
        if (sec == null) { Console.WriteLine("   sector read failed"); return; }
        foreach (int off in new[] { 0x0D0, 0x0DB, 0x0E6, 0x0F1 })
            Array.Copy(entry, 0, sec, off, entry.Length);
        ushort crc = OnboardProfiles.Crc16(sec.AsSpan(0, sec.Length - 2));
        sec[^2] = (byte)(crc >> 8); sec[^1] = (byte)(crc & 0xFF);
        Console.WriteLine($"   sector write: {(OnboardProfiles.WriteRawSector(d.Features, 0x0001, sec) ? "ok" : "FAILED")}");
        Console.WriteLine($"   swCtrl=1:     {(d.Features.Call(0x1300, 0x30, 0x01) == null ? "ERR" : "ok")}");
        Console.WriteLine($"   host mode:    {(d.Features.Call(FeatureId.OnboardProfiles, 0x10, 0x02) == null ? "ERR" : "ok")}");
        Console.WriteLine($"   verify 0x0D0: {Hex(OnboardProfiles.DumpSector(d.Features, 0x0001)?[0x0D0..0x0DB])}");
        Console.WriteLine($"   mode:         {Hex(d.Features.Call(FeatureId.OnboardProfiles, 0x20))}");
        Console.WriteLine($"   led1 state:   {Hex(d.Features.Call(0x1300, 0x40, 0x01))}");
    }

    if (args.Contains("ledbright"))
    {
        // Does the logo actually light? getState byte3 samples live brightness, so
        // sampling it repeatedly detects a running effect without human eyes.
        string Sample(int idx)
        {
            var vals = new List<string>();
            for (int i = 0; i < 6; i++)
            {
                byte[]? s = d.Features.Call(0x1300, 0x40, (byte)idx);
                vals.Add(s == null ? "??" : $"{s[1]:X2}:{s[2]:X2}{s[3]:X2}");
                Thread.Sleep(350);
            }
            return string.Join(" ", vals);
        }
        d.Features.Call(0x1300, 0x30, 0x01);
        var trials = new (string name, byte[] p)[]
        {
            ("u8 mode + bright + period", new byte[] { 0x01, 0x80, 0x00, 0xFF, 0x0B, 0xB8 }),
            ("u16 mode + bright + period", new byte[] { 0x01, 0x00, 0x80, 0x00, 0xFF, 0x0B, 0xB8 }),
            ("u16 mode + bright only",     new byte[] { 0x01, 0x00, 0x80, 0x00, 0xFF }),
            ("u8 mode 0x0A (profile enum)", new byte[] { 0x01, 0x0A, 0x00, 0xFF, 0x0B, 0xB8 }),
        };
        foreach (var (name, p) in trials)
        {
            var r = d.Features.Call(0x1300, 0x50, p);
            Console.WriteLine($"   {name}: {(r == null ? "REJECTED" : "accepted")}  -> {Sample(1)}");
        }
        Console.WriteLine($"   led0 ON  [0,00,01]: {(d.Features.Call(0x1300, 0x50, 0x00, 0x00, 0x01) == null ? "REJECTED" : "accepted")} -> {Sample(0)}");
    }

    if (args.Contains("ledmodes"))
    {
        string Sample(int idx)
        {
            var vals = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                byte[]? s = d.Features.Call(0x1300, 0x40, (byte)idx);
                vals.Add(s == null ? "??" : $"{s[2]:X2}{s[3]:X2}");
                Thread.Sleep(350);
            }
            return string.Join(" ", vals);
        }
        d.Features.Call(0x1300, 0x30, 0x01);
        void Try(string name, params byte[] p) =>
            Console.WriteLine($"   {name}: {(d.Features.Call(0x1300, 0x50, p) == null ? "REJECTED" : "ok")} -> logo {Sample(1)}");

        Try("logo breathing FF/3000", 0x01, 0x00, 0x80, 0x00, 0xFF, 0x0B, 0xB8);
        Try("logo fixed FF (period 0)", 0x01, 0x00, 0x80, 0x00, 0xFF, 0x00, 0x00);
        Try("logo dim 40 (period 0)", 0x01, 0x00, 0x80, 0x00, 0x40, 0x00, 0x00);
        Try("logo off (bright 0)", 0x01, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00);
        Try("logo mode ON 0x0001", 0x01, 0x00, 0x01, 0x00, 0xFF);
        Console.WriteLine($"   strip OFF: {(d.Features.Call(0x1300, 0x50, 0x00, 0x00, 0x02) == null ? "REJECTED" : "ok")} -> strip {Sample(0)}");
        Console.WriteLine($"   strip ON:  {(d.Features.Call(0x1300, 0x50, 0x00, 0x00, 0x01) == null ? "REJECTED" : "ok")} -> strip {Sample(0)}");
    }

    if (args.Contains("ledapp"))
    {
        // Exercise the exact path the GUI uses, sampling the live brightness.
        string Sample() { var v = new List<string>(); for (int i = 0; i < 5; i++) { var s = d.Features.Call(0x1300, 0x40, 0x01); v.Add(s == null ? "??" : $"{s[2]:X2}{s[3]:X2}"); Thread.Sleep(350); } return string.Join(" ", v); }
        void Run(string name, ProfileEdit.LightingConfig cfg)
            => Console.WriteLine($"   {name}: write={d.WriteLighting(cfg)} logo=[{Sample()}]");

        Run("Breathing #00FFFF 3000ms", new ProfileEdit.LightingConfig(ProfileEdit.LedBreathing, 0, 0xFF, 0xFF, 3000));
        Run("Fixed  #00FFFF",           new ProfileEdit.LightingConfig(ProfileEdit.LedFixed, 0, 0xFF, 0xFF, 3000));
        Run("Fixed  dim #004040",       new ProfileEdit.LightingConfig(ProfileEdit.LedFixed, 0, 0x40, 0x40, 3000));
        Run("Off",                      new ProfileEdit.LightingConfig(ProfileEdit.LedOff, 0, 0, 0, 3000));
        Run("Breathing again",          new ProfileEdit.LightingConfig(ProfileEdit.LedBreathing, 0, 0xFF, 0xFF, 3000));
    }

    if (args.Contains("strip"))
    {
        string S(int idx) { var s = d.Features.Call(0x1300, 0x40, (byte)idx); return s == null ? "??" : $"{s[1]:X2}{s[2]:X2}:{s[3]:X2}"; }
        d.Features.Call(0x1300, 0x30, 0x01);
        void T(string name, params byte[] p)
            => Console.WriteLine($"   {name}: {(d.Features.Call(0x1300, 0x50, p) == null ? "REJECTED" : "ok")} state={S(0)}");
        T("on 3-byte      ", 0x00, 0x00, 0x01);
        T("on +bright     ", 0x00, 0x00, 0x01, 0x00, 0xFF);
        T("on +bright+per ", 0x00, 0x00, 0x01, 0x00, 0xFF, 0x00, 0x00);
        T("breathing FF   ", 0x00, 0x00, 0x80, 0x00, 0xFF, 0x0B, 0xB8);
        T("mode 0x0004    ", 0x00, 0x00, 0x04, 0x00, 0xFF, 0x00, 0x00);
    }

    if (args.Contains("fadetest"))
    {
        // Does driving live BEFORE the flash write avoid the ramp-from-zero?
        string Sample() { var v = new List<string>(); for (int i = 0; i < 5; i++) { var s = d.Features.Call(0x1300, 0x40, 0x01); v.Add(s == null ? "??" : $"{s[3]:X2}"); Thread.Sleep(300); } return string.Join(" ", v); }
        d.Features.Call(0x1300, 0x30, 0x01);
        LedControl.SetState(d.Features, 1, LedControl.ModeBreathing, 0xFF, 0);
        Thread.Sleep(2500);
        Console.WriteLine($"   steady bright:   {Sample()}");
        LedControl.SetState(d.Features, 1, LedControl.ModeBreathing, 0x40, 0);
        Console.WriteLine($"   -> dim (live):   {Sample()}   (want a smooth fall, not 00 first)");
    }

    if (args.Contains("fade2"))
    {
        string Sample() { var v = new List<string>(); for (int i = 0; i < 5; i++) { var s = d.Features.Call(0x1300, 0x40, 0x01); v.Add(s == null ? "??" : $"{s[3]:X2}"); Thread.Sleep(300); } return string.Join(" ", v); }
        ProfileEdit.LightingConfig C(int mode, byte shade) => new(mode, 0, shade, shade, 3000);
        d.WriteLighting(C(ProfileEdit.LedFixed, 0xFF));
        Thread.Sleep(2500);
        Console.WriteLine($"   bright (settled): {Sample()}");
        d.WriteLighting(C(ProfileEdit.LedFixed, 0x60));                       // persists (flash)
        Console.WriteLine($"   -> dim, persist:  {Sample()}");
        Thread.Sleep(1500);
        d.WriteLighting(C(ProfileEdit.LedFixed, 0xFF), persist: false);       // per-app path
        Console.WriteLine($"   -> bright, live:  {Sample()}");
    }

    if (args.Contains("ledread"))
    {
        var v = new List<string>();
        for (int i = 0; i < 3; i++) { var s = d.Features.Call(0x1300, 0x40, 0x01); v.Add(s == null ? "??" : $"{s[3]:X2}"); Thread.Sleep(300); }
        Console.WriteLine($"   LOGO={string.Join(" ", v)}");
    }

    if (args.Contains("fwtest"))
    {
        // What does the logo actually do once the firmware owns the LEDs?
        string Sample(int n) { var v = new List<string>(); for (int i = 0; i < n; i++) { var s = d.Features.Call(0x1300, 0x40, 0x01); v.Add(s == null ? "??" : $"{s[3]:X2}"); Thread.Sleep(400); } return string.Join(" ", v); }
        d.WriteLighting(new ProfileEdit.LightingConfig(ProfileEdit.LedFixed, 0, 0x60, 0x60, 3000));
        Thread.Sleep(2000);
        Console.WriteLine($"   software fixed:  {Sample(4)}");
        d.WriteLighting(new ProfileEdit.LightingConfig(ProfileEdit.LedOff, 0, 0, 0, 3000, FirmwareMode: true));
        Console.WriteLine($"   firmware mode:   {Sample(12)}  (varying = breathing, flat = stuck)");
        Console.WriteLine($"   swCtrl: {Hex(d.Features.Call(0x1300, 0x20))}");
    }

    if (args.Contains("layouttest"))
    {
        foreach (char c in "şıİğüöç")
        {
            var (k, m) = Macro.CharToKeyLayout(c);
            Console.WriteLine($"   '{c}' -> hid=0x{k:X2} mods=0x{m:X2} {(k == 0 ? "UNMAPPED" : "ok")}");
        }
    }

    if (args.Contains("repeatenc"))
    {
        var steps = new List<Macro.Step> { new(Macro.Kind.KeyDown, 0, 0x0B), new(Macro.Kind.KeyUp, 0, 0x0B) }; // "h"
        Console.WriteLine($"   Once:      {Hex(Macro.Encode(steps, Macro.RepeatMode.Once))}");
        Console.WriteLine($"   WhileHeld: {Hex(Macro.Encode(steps, Macro.RepeatMode.WhileHeld))}  (expect ..44 00 0B 02 FF)");
        Console.WriteLine($"   Toggle:    {Hex(Macro.Encode(steps, Macro.RepeatMode.Toggle))}  (expect ..44 00 0B 03 FF)");
    }

    if (args.Contains("repeatwrite") && d.Buttons.Count >= 8)
    {
        const int idx = 8;
        byte[]? origBtn = OnboardProfiles.ReadButtonBytes(d.Features, idx);
        int? ms = OnboardProfiles.FindMacroSector(d.Features);
        byte[]? origSec = ms == null ? null : OnboardProfiles.DumpSector(d.Features, ms.Value);

        var steps = new List<Macro.Step> { new(Macro.Kind.KeyDown, 0, 0x0B), new(Macro.Kind.KeyUp, 0, 0x0B) };
        var (ok, sec, addr, err) = d.AssignMacro(idx, steps, Macro.RepeatMode.WhileHeld);
        Console.WriteLine($"   WhileHeld write -> ok={ok} sec=0x{sec:X3} addr=0x{addr:X3} err={err}");
        if (sec != null && addr != null)
        {
            byte[]? s2 = OnboardProfiles.DumpSector(d.Features, sec.Value);
            int a = addr.Value;
            Console.WriteLine($"   stored: {Hex(s2?[a..Math.Min(a + 8, s2.Length)])}  (want 43 00 0B 44 00 0B 02 FF)");
            Console.WriteLine($"   label:  {d.Buttons.First(b => b.Index == idx).Label}");
        }
        if (ms != null && origSec != null) OnboardProfiles.WriteRawSector(d.Features, ms.Value, origSec);
        if (origBtn != null) d.RemapButton(idx, origBtn);
        Console.WriteLine($"   restored button {idx}");
    }

    if (args.Contains("stripe"))
    {
        string St(int i) { var s = d.Features.Call(0x1300, 0x40, (byte)i); return s == null ? "??" : Hex(s[..6]); }
        Console.WriteLine($"   swCtrl now: {Hex(d.Features.Call(0x1300, 0x20))}");
        Console.WriteLine($"   led0 stripes: {St(0)}");
        Console.WriteLine($"   led1 logo:    {St(1)}");
        // Hand to firmware, watch the stripes for a few seconds
        d.Features.Call(0x1300, 0x30, 0x00);
        for (int i = 0; i < 4; i++) { Console.WriteLine($"   fw t{i}: stripes={St(0)}"); Thread.Sleep(400); }
        // Now change DPI and see if the stripes react (indicator behaviour)
        Console.WriteLine("   -- changing DPI 800 then 3200 --");
        d.Features.Call(FeatureId.AdjustableDpi, 0x30, 0x00, 0x03, 0x20); Thread.Sleep(500); Console.WriteLine($"   @800:  stripes={St(0)}");
        d.Features.Call(FeatureId.AdjustableDpi, 0x30, 0x00, 0x0C, 0x80); Thread.Sleep(500); Console.WriteLine($"   @3200: stripes={St(0)}");
    }

    if (args.Contains("dumpall"))
    {
        for (int s = 0; s <= 2; s++)
        {
            byte[]? sec = OnboardProfiles.DumpSector(d.Features, s);
            Console.WriteLine($"   === sector {s} ===");
            if (sec == null) { Console.WriteLine("   <unreadable>"); continue; }
            for (int row = 0; row < sec.Length; row += 16)
            {
                var slice = sec[row..Math.Min(row + 16, sec.Length)];
                if (Array.TrueForAll(slice, x => x == 0xFF)) continue;
                Console.WriteLine($"   {s}:{row:X3}: {string.Join(" ", slice.Select(x => x.ToString("X2")))}");
            }
        }
        Console.WriteLine($"   swCtrl: {Hex(d.Features.Call(0x1300, 0x20))}");
        Console.WriteLine($"   led0:   {Hex(d.Features.Call(0x1300, 0x40, 0x00))}");
        Console.WriteLine($"   led1:   {Hex(d.Features.Call(0x1300, 0x40, 0x01))}");
    }

    if (args.Contains("stripeon"))
    {
        string St() { var s = d.Features.Call(0x1300, 0x40, 0x00); return s == null ? "??" : Hex(s[..4]); }
        Console.WriteLine($"   before: led0={St()}  swCtrl={Hex(d.Features.Call(0x1300, 0x20)?[..2])}");
        void T(string name, params byte[] p)
            => Console.WriteLine($"   {name}: {(d.Features.Call(0x1300, 0x50, p) == null ? "REJECTED" : "ok")} -> led0={St()}");
        T("[0,0002]          ", 0x00, 0x00, 0x02);
        T("[0,0002,00FF]     ", 0x00, 0x00, 0x02, 0x00, 0xFF);
        T("[0,0002,00FF,0000]", 0x00, 0x00, 0x02, 0x00, 0xFF, 0x00, 0x00);
        // restore the user's DPI (stripe probe left it at 3200)
        d.Features.Call(FeatureId.AdjustableDpi, 0x30, 0x00, 0x06, 0x40);
        Console.WriteLine($"   dpi restored to 1600");
    }

    if (args.Contains("features"))
    {
        byte[]? cnt = d.Features.Call(0x0001, 0x00);
        int n = cnt != null && cnt.Length > 0 ? cnt[0] : 0;
        Console.WriteLine($"   feature count: {n}");
        for (byte i = 1; i <= n; i++)
        {
            byte[]? e = d.Features.Call(0x0001, 0x10, i);
            if (e == null || e.Length < 3) continue;
            int fid = (e[0] << 8) | e[1];
            Console.WriteLine($"   idx {i:X2}: 0x{fid:X4} flags={e[2]:X2}");
        }
    }

    if (args.Contains("stripeon2"))
    {
        string St() { var s = d.Features.Call(0x1300, 0x40, 0x00); return s == null ? "??" : Hex(s[..4]); }
        // ENABLE_HIDDEN_FEATURES gate, then retry the always-on write
        bool hasHidden = d.Features.Has(0x1E00);
        Console.WriteLine($"   has 0x1E00: {hasHidden}");
        if (hasHidden)
            Console.WriteLine($"   enable hidden: {(d.Features.Call(0x1E00, 0x10, 0x01) == null ? "REJECTED" : "ok")}");
        void T(string name, byte fn, params byte[] p)
            => Console.WriteLine($"   {name}: {(d.Features.Call(0x1300, fn, p) == null ? "REJECTED" : "ok")} -> led0={St()}");
        T("f50 [0,0002]", 0x50, 0x00, 0x00, 0x02);
        T("f60 [0,0002]", 0x60, 0x00, 0x00, 0x02);
        T("f70 [0,0002]", 0x70, 0x00, 0x00, 0x02);
    }

    if (args.Contains("stripeon3"))
    {
        d.Features.Call(0x1E00, 0x10, 0x01);
        string St() { var s = d.Features.Call(0x1300, 0x40, 0x00); return s == null ? "??" : Hex(s[..4]); }
        void R(string name, byte fn, params byte[] p)
        {
            var r = d.Features.Call(0x1300, fn, p);
            Console.WriteLine($"   {name}: {(r == null ? "REJECTED" : Hex(r[..6]))} led0={St()}");
        }
        R("f60 read []   ", 0x60);
        R("f60 read [0]  ", 0x60, 0x00);
        R("f60 read [1]  ", 0x60, 0x01);
        R("f50 full 0002 ", 0x50, 0x00, 0x00, 0x02, 0x00, 0xFF, 0x00, 0x00);
        R("f60 [0,02]    ", 0x60, 0x00, 0x02);
        R("f60 [0,0,2,ff]", 0x60, 0x00, 0x00, 0x02, 0x00, 0xFF);
        R("f60 [0,0002,ff,per]", 0x60, 0x00, 0x00, 0x02, 0x00, 0xFF, 0x00, 0x00);
        R("f60 read [0] again", 0x60, 0x00);
    }

    if (args.Contains("stripeon4"))
    {
        d.Features.Call(0x1E00, 0x10, 0x01);
        string St() { var s = d.Features.Call(0x1300, 0x40, 0x00); return s == null ? "??" : Hex(s[..4]); }
        void T(string name, byte fn, params byte[] p)
            => Console.WriteLine($"   {name}: {(d.Features.Call(0x1300, fn, p) == null ? "REJECTED" : "ok")} led0={St()}");
        d.Features.Call(0x1300, 0x30, 0x01);   // software control
        Console.WriteLine("   -- swCtrl=1 (hidden enabled) --");
        T("f50 [0,0002]     ", 0x50, 0x00, 0x00, 0x02);
        T("f50 [0,0001]     ", 0x50, 0x00, 0x00, 0x01);
        T("f70 [0,0002]     ", 0x70, 0x00, 0x00, 0x02);
        T("f70 [0,04]       ", 0x70, 0x00, 0x04);
        T("f70 [0,00]       ", 0x70, 0x00, 0x00);
        d.Features.Call(0x1300, 0x30, 0x00);   // back to firmware
        Console.WriteLine($"   -- swCtrl=0 -- led0={St()}");
    }

    if (args.Contains("nvread"))
    {
        d.Features.Call(0x1E00, 0x10, 0x01);
        Console.WriteLine($"   nv led0: {Hex(d.Features.Call(0x1300, 0x60, 0x00)?[..4])}");
        Console.WriteLine($"   nv led1: {Hex(d.Features.Call(0x1300, 0x60, 0x01)?[..4])}");
        Console.WriteLine($"   st led0: {Hex(d.Features.Call(0x1300, 0x40, 0x00)?[..4])}");
        Console.WriteLine($"   st led1: {Hex(d.Features.Call(0x1300, 0x40, 0x01)?[..4])}");
        Console.WriteLine($"   swCtrl:  {Hex(d.Features.Call(0x1300, 0x20)?[..2])}");
    }

    if (args.Contains("nvwrite"))
    {
        d.Features.Call(0x1E00, 0x10, 0x01);
        string Nv() { return Hex(d.Features.Call(0x1300, 0x60, 0x00)?[..2]); }
        Console.WriteLine($"   nv before: {Nv()}");
        Console.WriteLine($"   set 0x04 (indicator): {(d.Features.Call(0x1300, 0x70, 0x00, 0x04) == null ? "REJECTED" : "ok")} -> nv={Nv()}");
        Thread.Sleep(2000);
        Console.WriteLine($"   set 0x02 (always on): {(d.Features.Call(0x1300, 0x70, 0x00, 0x02) == null ? "REJECTED" : "ok")} -> nv={Nv()}");
    }

    if (args.Contains("nvapp"))
    {
        string Nv() { d.Features.Call(0x1E00, 0x10, 0x01); return Hex(d.Features.Call(0x1300, 0x60, 0x00)?[..2]); }
        var fwOff = new ProfileEdit.LightingConfig(ProfileEdit.LedOff, 0, 0, 0, 3000, FirmwareMode: true, StripAlwaysOn: false);
        var fwOn = new ProfileEdit.LightingConfig(ProfileEdit.LedOff, 0, 0, 0, 3000, FirmwareMode: true, StripAlwaysOn: true);
        Console.WriteLine($"   indicator: write={d.WriteLighting(fwOff)} nv={Nv()}  (want 00 04)");
        Thread.Sleep(1500);
        Console.WriteLine($"   always-on: write={d.WriteLighting(fwOn)} nv={Nv()}  (want 00 02)");
    }

    if (args.Contains("readmacros"))
    {
        int? ms = OnboardProfiles.FindMacroSector(d.Features);
        Console.WriteLine($"\n   MACRO SECTOR 0x{ms:X3} (non-blank rows):");
        byte[]? data = ms == null ? null : OnboardProfiles.DumpSector(d.Features, ms.Value);
        if (data != null)
            for (int row = 0; row < data.Length; row += 16)
            {
                var slice = data[row..Math.Min(row + 16, data.Length)];
                if (Array.TrueForAll(slice, x => x == 0xFF)) continue;
                string hex = string.Join(" ", slice.Select(x => x.ToString("X2")));
                Console.WriteLine($"   0x{row:X3}: {hex}");
            }
    }

    if (args.Contains("sectors"))
    {
        var info = OnboardProfiles.ReadInfo(d.Features);
        Console.WriteLine($"\n   SECTORS: count={info?.Sectors}, size={info?.Size}, buttons={info?.Buttons}");
        for (int s = 0; info != null && s < info.Sectors; s++)
        {
            byte[]? data = OnboardProfiles.DumpSector(d.Features, s);
            if (data == null) { Console.WriteLine($"   sector 0x{s:X3}: <unreadable>"); continue; }
            bool blankFF = Array.TrueForAll(data, x => x == 0xFF);
            bool blank00 = Array.TrueForAll(data, x => x == 0x00);
            string tag = blankFF ? "ALL 0xFF (free)" : blank00 ? "ALL 0x00" : "used";
            Console.WriteLine($"   sector 0x{s:X3}: {tag}  first8={Hex(data[..8])}");
        }
    }

    if (args.Contains("repeattest") && d.Buttons.Count >= 8)
    {
        const int idx = 8;
        Console.WriteLine("\n   REPEAT-MODE WRITE TEST (WhileHeld)");
        int? ms2 = OnboardProfiles.FindMacroSector(d.Features);
        byte[]? origBtn = OnboardProfiles.ReadButtonBytes(d.Features, idx);
        byte[]? origSec = ms2 == null ? null : OnboardProfiles.DumpSector(d.Features, ms2.Value);

        var steps = new List<Macro.Step> { new(Macro.Kind.KeyDown, 0, 0x0A), new(Macro.Kind.KeyUp, 0, 0x0A) };
        var (ok, sec, addr, _) = d.AssignMacro(idx, steps, Macro.RepeatMode.WhileHeld);
        Console.WriteLine($"   wrote WhileHeld macro -> ok={ok}, sector=0x{sec:X3}, addr=0x{addr:X3}");
        if (sec != null && addr != null)
        {
            byte[]? s2 = OnboardProfiles.DumpSector(d.Features, sec.Value);
            int a = addr.Value;
            Console.WriteLine($"   stored bytes: {Hex(s2?[a..(a + 8)])}   (expect 02 prefix)");
        }
        if (ms2 != null && origSec != null) OnboardProfiles.WriteRawSector(d.Features, ms2.Value, origSec);
        if (origBtn != null) d.RemapButton(idx, origBtn);
        Console.WriteLine($"   restored button {idx} -> {d.Buttons.First(b => b.Index == idx).Label}");
    }

    if (args.Contains("macrotest") && d.Buttons.Count >= 8)
    {
        const int idx = 8; // Win+E on the G402
        Console.WriteLine("\n   MACRO TEST");
        int? macroSector = OnboardProfiles.FindMacroSector(d.Features);
        Console.WriteLine($"   macro sector: 0x{macroSector:X3}");

        // full backups so we restore byte-for-byte
        byte[]? origBtn = OnboardProfiles.ReadButtonBytes(d.Features, idx);
        byte[]? origSector = macroSector == null ? null : OnboardProfiles.DumpSector(d.Features, macroSector.Value);

        var steps = Macro.TypeText("hi");
        Console.WriteLine($"   encoding macro 'hi' -> {Hex(Macro.Encode(steps))}");

        var (ok, sector, address, _) = d.AssignMacro(idx, steps);
        Console.WriteLine($"   assigned to button {idx} -> ok={ok}, sector=0x{sector:X3}, address=0x{address:X3}");
        if (sector != null && address != null)
        {
            byte[]? sec = OnboardProfiles.DumpSector(d.Features, sector.Value);
            int a = address.Value;
            Console.WriteLine($"   macro bytes at 0x{a:X3}: {Hex(sec?[a..(a + 13)])}");
        }
        Console.WriteLine($"   button {idx} now reads: {d.Buttons.First(b => b.Index == idx).Label}");

        // restore byte-for-byte
        if (macroSector != null && origSector != null)
            OnboardProfiles.WriteRawSector(d.Features, macroSector.Value, origSector);
        if (origBtn != null) d.RemapButton(idx, origBtn);
        Console.WriteLine($"   restored button {idx} -> {d.Buttons.First(b => b.Index == idx).Label}");
    }

    Console.WriteLine();
    d.Dispose();
}

static string Hex(byte[]? b) => b == null ? "<null>" : string.Join(" ", b.Select(x => x.ToString("X2")));

static class Native
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string cls, string? win);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr w, StringBuilder l);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
