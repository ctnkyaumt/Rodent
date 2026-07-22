using System.Text.Json;

namespace Rodent.Core.Automation;

// New kinds are appended so existing profiles.json enum values stay stable.
public enum BindingKind { Default, MouseClick, KeyChord, TypeText, LaunchApp, System, RepeatText, Macro, Dpi }

/// <summary>System actions available to a binding (media / volume / session).</summary>
public static class SystemActions
{
    public const string VolumeUp = "Volume Up";
    public const string VolumeDown = "Volume Down";
    public const string Mute = "Mute";
    public const string PlayPause = "Play / Pause";
    public const string NextTrack = "Next Track";
    public const string PrevTrack = "Previous Track";
    public const string LockPc = "Lock PC";

    public static readonly string[] All =
        { VolumeUp, VolumeDown, Mute, PlayPause, NextTrack, PrevTrack, LockPc };
}

/// <summary>What one mouse button does inside an app profile (host-side).</summary>
public sealed class ButtonBinding
{
    public BindingKind Kind { get; set; } = BindingKind.Default;
    public string Text { get; set; } = "";           // click name / typed text / path / system action / chord text / macro name
    public ushort VirtualKey { get; set; }            // KeyChord
    public ushort[] Modifiers { get; set; } = Array.Empty<ushort>();
    public List<Rodent.Core.Hidpp.Macro.Step>? MacroSteps { get; set; }  // Macro: editor steps, played host-side
    public int MacroRepeat { get; set; }              // Macro: (int)Macro.RepeatMode

    public string Describe() => Kind switch
    {
        BindingKind.Default => "Default",
        BindingKind.MouseClick => Text,
        BindingKind.KeyChord => Text,
        BindingKind.TypeText => $"Type: {Text}",
        BindingKind.RepeatText => $"Repeat: {Text}",
        BindingKind.Macro => string.IsNullOrWhiteSpace(Text) ? "Macro" : Text,
        BindingKind.Dpi => Text,
        BindingKind.LaunchApp => $"Launch: {System.IO.Path.GetFileNameWithoutExtension(Text)}",
        BindingKind.System => Text,
        _ => "?",
    };
}

/// <summary>Lighting a profile switches to when its app comes forward.</summary>
public sealed class LightingSetting
{
    public int Mode { get; set; } = 0x0A;             // ProfileEdit: 0x00 Off, 0x01 Fixed, 0x0A Breathing
    public int Shade { get; set; } = 255;             // 0-255 brightness of the single-colour LED
    public int PeriodMs { get; set; } = 3000;
    public bool FirmwareMode { get; set; }            // mouse drives both LEDs (DPI stripes + factory breathing)
    public bool StripAlwaysOn { get; set; }           // DPI stripes stay lit, not just on DPI change
}

/// <summary>
/// One per-app profile: when App is in the foreground, mouse buttons 4-8 run these
/// bindings on the host and (if set) the mouse switches to this profile's lighting.
/// App "*" is the fallback profile used everywhere else.
/// </summary>
public sealed class AppProfile
{
    public string Name { get; set; } = "";
    public string App { get; set; } = "*";            // exe name lowercase without .exe
    public Dictionary<int, ButtonBinding> Buttons { get; set; } = new();
    public LightingSetting? Lighting { get; set; }

    public ButtonBinding? Get(int button) =>
        Buttons.TryGetValue(button, out var b) && b.Kind != BindingKind.Default ? b : null;
}

/// <summary>
/// The per-app profile set, persisted as JSON in %AppData%\Rodent. While Enabled,
/// the mouse's onboard buttons 4-8 are remapped to signal keys F13-F17 (backed up
/// in HwBackup) and the host translates them per the foreground app.
/// </summary>
public sealed class ProfilesConfig
{
    public const int FirstButton = 4;                 // 1-3 stay physical clicks
    public const int LastButton = 8;

    public bool Enabled { get; set; }
    public List<AppProfile> Profiles { get; set; } = new();
    public Dictionary<int, string> HwBackup { get; set; } = new(); // button -> hex of original 4 bytes

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rodent", "profiles.json");

    public static ProfilesConfig Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<ProfilesConfig>(File.ReadAllText(Path)) ?? new();
        }
        catch { /* fall through to empty */ }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>Fallback profile ("*"), created on demand.</summary>
    public AppProfile Wildcard()
    {
        var w = Profiles.FirstOrDefault(p => p.App == "*");
        if (w == null)
        {
            w = new AppProfile { Name = "Desktop (default)", App = "*" };
            Profiles.Insert(0, w);
        }
        return w;
    }

    /// <summary>Binding to run for a button in an app: app profile first, then "*".</summary>
    public ButtonBinding? Resolve(string app, int button)
    {
        var exact = Profiles.FirstOrDefault(p => p.App == app && p.App != "*");
        return exact?.Get(button) ?? Profiles.FirstOrDefault(p => p.App == "*")?.Get(button);
    }

    /// <summary>Lighting for an app: its own profile first, then "*". Null = leave as is.</summary>
    public LightingSetting? ResolveLighting(string app)
    {
        var exact = Profiles.FirstOrDefault(p => p.App == app && p.App != "*");
        return exact?.Lighting ?? Profiles.FirstOrDefault(p => p.App == "*")?.Lighting;
    }
}
