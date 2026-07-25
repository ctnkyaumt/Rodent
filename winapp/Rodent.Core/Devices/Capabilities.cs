using Rodent.Core.Hidpp;

namespace Rodent.Core.Devices;

/// <summary>One remappable button as the rest of Rodent sees it — no protocol in it.</summary>
public sealed record DeviceButton(int Index, string Label, bool IsMacro = false);

/// <summary>
/// Devices whose buttons can be listed and reassigned.
///
/// The action passed to <see cref="RemapButton"/> is in the driver's own encoding
/// (HID++ uses 4 bytes); callers get those bytes from <see cref="ReadButtonBytes"/>
/// or from a driver-supplied catalog, so they never have to build them blind.
/// </summary>
public interface IButtonDevice : IDeviceDriver
{
    IReadOnlyList<DeviceButton> Buttons { get; }

    /// <summary>Current action of a button (1-based) in the driver's encoding — for backups.</summary>
    byte[]? ReadButtonBytes(int index1Based);

    /// <summary>Reassign a button (1-based). Returns success plus the label it now reads back as.</summary>
    (bool ok, string label) RemapButton(int index1Based, byte[] action);

    /// <summary>
    /// True when the device is executing its own stored config, which is what makes
    /// remapped buttons fire. Devices with no host/onboard split return true.
    /// </summary>
    bool IsOnboardMode();

    /// <summary>Put the device into onboard mode. Returns the resulting state.</summary>
    bool EnableOnboardMode();
}

/// <summary>Devices that can store a macro and point a button at it.</summary>
public interface IMacroDevice : IDeviceDriver
{
    /// <summary>
    /// Store <paramref name="steps"/> on the device and bind button
    /// <paramref name="index1Based"/> to it. sector/address are where it landed
    /// (diagnostics); error is a short human-readable reason on failure.
    /// </summary>
    (bool ok, int? sector, int? address, string? error) AssignMacro(
        int index1Based, IReadOnlyList<Macro.Step> steps, Macro.RepeatMode repeat = Macro.RepeatMode.Once);
}

/// <summary>Devices with controllable lighting.</summary>
public interface ILightingDevice : IDeviceDriver
{
    ProfileEdit.LightingConfig? ReadLighting();

    /// <summary>persist=false drives the LEDs without a flash write (per-app switching).</summary>
    bool WriteLighting(ProfileEdit.LightingConfig cfg, bool persist = true);
}

/// <summary>Devices exposing DPI stages beyond the plain "current DPI" setting.</summary>
public interface IDpiDevice : IDeviceDriver
{
    /// <summary>DPI values the sensor accepts (for slider snapping). Empty if unknown.</summary>
    List<int> DpiChoices();

    ProfileEdit.DpiConfig? ReadDpiProfile();
    bool WriteDpiProfile(ProfileEdit.DpiConfig cfg);

    /// <summary>Run a host-side DPI binding: "DPI +", "DPI -", "Cycle DPI", "DPI Shift (sniper)".</summary>
    void DpiAction(string action, bool down);
}

/// <summary>
/// Raw protocol access for diagnostics (Rodent.Probe). Implemented by HID++ 2.0
/// devices; other brands answer their own protocols, so tools that poke feature
/// registers ask for this capability rather than for a Logitech device.
/// </summary>
public interface IHidppDevice : IDeviceDriver
{
    FeatureTable Features { get; }
}
