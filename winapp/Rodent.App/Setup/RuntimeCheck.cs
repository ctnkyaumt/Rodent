using System.IO;
using Microsoft.Win32;
using Rodent.Core.Diagnostics;

namespace Rodent.App.Setup;

/// <summary>
/// Rodent ships framework-dependent — a ~10 MB download instead of ~120 MB — so
/// it needs the .NET Desktop Runtime on the machine.
///
/// When the runtime is missing entirely, none of this code runs: the apphost
/// shows Windows' own "install the .NET Desktop Runtime" dialog and exits. This
/// check covers the case that dialog cannot: the exe found a *private* runtime
/// (a developer's SDK under %USERPROFILE%\.dotnet, or DOTNET_ROOT pointing
/// somewhere) so it starts, but the copy we are about to install — launched from
/// the Start menu or at boot, without that environment — would find nothing.
/// Installing in that state would produce an app that only starts sometimes.
/// </summary>
internal static class RuntimeCheck
{
    public const int RequiredMajor = 8;

    public const string DownloadUrl =
        "https://dotnet.microsoft.com/download/dotnet/8.0/runtime?runtime=windowsdesktop";

    /// <summary>
    /// True when a machine-wide Windows Desktop runtime of at least
    /// <see cref="RequiredMajor"/> is installed. <paramref name="found"/> reports
    /// the highest version seen, or null.
    /// </summary>
    public static bool IsDesktopRuntimeInstalled(out string? found)
    {
        found = null;
        Version? best = null;

        foreach (string root in MachineWideRoots())
        {
            string dir = Path.Combine(root, "shared", "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(dir)) continue;
            foreach (string versionDir in SafeDirectories(dir))
            {
                string name = Path.GetFileName(versionDir);
                // Strip any prerelease suffix: 8.0.0-rc.1 -> 8.0.0
                int dash = name.IndexOf('-');
                if (dash > 0) name = name[..dash];
                if (Version.TryParse(name, out var v) && (best == null || v > best)) best = v;
            }
        }

        if (best != null) found = best.ToString();
        return best != null && best.Major >= RequiredMajor;
    }

    /// <summary>
    /// Places a runtime counts as "installed for everyone". Deliberately excludes
    /// DOTNET_ROOT and %USERPROFILE%\.dotnet — those are exactly the private
    /// installs this check exists to catch.
    /// </summary>
    private static IEnumerable<string> MachineWideRoots()
    {
        string? registered = null;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64");
            registered = key?.GetValue("InstallLocation") as string;
        }
        catch { /* not registered; fall back to the well-known paths */ }

        if (!string.IsNullOrWhiteSpace(registered)) yield return registered!;

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
    }

    private static string[] SafeDirectories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Log what we are running on — the first thing to know from a bug report.</summary>
    public static void LogState()
    {
        bool ok = IsDesktopRuntimeInstalled(out string? found);
        Log.Info($"desktop runtime (machine-wide): {(found ?? "none")}{(ok ? "" : " — below the required major " + RequiredMajor)}");
    }
}
