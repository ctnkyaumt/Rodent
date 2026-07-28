using System.Text;
using Rodent.App.Setup;
using Rodent.Core.Devices;
using Rodent.Core.Diagnostics;
using Rodent.Core.Model;

namespace Rodent.App.Cli;

/// <summary>
/// Commands that finish without ever showing a window. Returns an exit code, or
/// null when the request needs the GUI (normal launch, interactive installer).
/// </summary>
internal static class CliRunner
{
    public static int? Run(CliOptions o)
    {
        if (o.Errors.Count > 0)
        {
            foreach (string e in o.Errors) ConsoleHost.Error("rodent: " + e);
            ConsoleHost.Error("Try 'Rodent.exe --help'.");
            ConsoleHost.Finish();
            return 2;
        }

        if (o.Help) { ConsoleHost.Out(CliOptions.HelpText); ConsoleHost.Finish(); return 0; }
        if (o.Version)
        {
            RuntimeCheck.IsDesktopRuntimeInstalled(out string? runtime);
            ConsoleHost.Out($"Rodent {App.VersionTag}\n" +
                            $"running on .NET {Environment.Version}\n" +
                            $"desktop runtime (machine-wide): {runtime ?? "none"}");
            ConsoleHost.Finish();
            return 0;
        }
        if (o.ShowLogPath) { ConsoleHost.Out(LogPathReport()); ConsoleHost.Finish(); return 0; }
        if (o.ListDevices) return ListDevices();

        // Interactive install/uninstall are handled by the GUI (App.OnStartup).
        if (o.Install && o.Silent) return SilentInstall(o);
        if (o.Uninstall && o.Silent) return SilentUninstall(o);

        return null;
    }

    private static string LogPathReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"level:     {Log.Level}");
        sb.AppendLine($"log file:  {Log.FilePath ?? "(disabled)"}");
        sb.Append($"directory: {Log.DefaultDirectory}");
        return sb.ToString();
    }

    private static int ListDevices()
    {
        ConsoleHost.Attach();
        List<IDeviceDriver> devices;
        try
        {
            devices = DeviceManager.Discover();
        }
        catch (Exception ex)
        {
            Log.Exception(ex, "--list");
            ConsoleHost.Error("Device scan failed: " + ex.Message);
            ConsoleHost.Finish();
            return 1;
        }

        if (devices.Count == 0)
        {
            ConsoleHost.Out("No supported devices found.");
            ConsoleHost.Finish();
            return 1;
        }

        var sb = new StringBuilder();
        foreach (var d in devices)
        {
            sb.AppendLine($"{d.Name}  [{d.Brand} {d.VendorId:X4}:{d.ProductId:X4}]  {d.Kind}, support: {d.Support}");
            foreach (var info in d.Info)
                sb.AppendLine($"    {info.Label}: {info.Value}");
            foreach (var s in d.Settings)
                sb.AppendLine($"    {s.Label} = {Describe(s)}");
            d.Dispose();
        }
        ConsoleHost.Out(sb.ToString().TrimEnd());
        ConsoleHost.Finish();
        return 0;
    }

    /// <summary>Current value of a setting, rendered the way the UI labels it.</summary>
    private static string Describe(Setting s)
    {
        try
        {
            switch (s)
            {
                case ToggleSetting t:
                    return t.Read() switch { true => "on", false => "off", _ => "?" };
                case RangeSetting r:
                    return r.Read()?.ToString() ?? "?";
                case ChoiceSetting c:
                    int? raw = c.Read();
                    if (raw == null) return "?";
                    foreach (var choice in c.Choices)
                        if (choice.Value == raw) return choice.Label;
                    return raw.Value.ToString();
                default:
                    return "?";
            }
        }
        catch (Exception ex)
        {
            Log.Exception(ex, $"reading setting {s.Name}");
            return "(read failed)";
        }
    }

    private static int SilentInstall(CliOptions o)
    {
        // Same rule as the GUI: never install against a runtime only this shell
        // can see (see RuntimeCheck). Exit 3 = prerequisite missing.
        if (!RuntimeCheck.IsDesktopRuntimeInstalled(out string? found))
        {
            ConsoleHost.Error(
                $"Microsoft .NET Desktop Runtime {RuntimeCheck.RequiredMajor} (x64) is not installed for this PC " +
                $"(highest found: {found ?? "none"}). Nothing was installed.");
            ConsoleHost.Error(RuntimeCheck.DownloadUrl);
            ConsoleHost.Finish();
            return 3;
        }

        try
        {
            Installer.Install(o.Startup ?? false, o.DesktopShortcut, s => ConsoleHost.Out("  " + s));
            ConsoleHost.Out($"Installed to {Installer.InstallRoot}");
            ConsoleHost.Finish();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Exception(ex, "silent install");
            ConsoleHost.Error("Install failed: " + ex.Message);
            ConsoleHost.Finish();
            return 1;
        }
    }

    private static int SilentUninstall(CliOptions o)
    {
        try
        {
            Installer.Uninstall(o.Purge, s => ConsoleHost.Out("  " + s));
            ConsoleHost.Out("Rodent removed.");
            ConsoleHost.Finish();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Exception(ex, "silent uninstall");
            ConsoleHost.Error("Uninstall failed: " + ex.Message);
            ConsoleHost.Finish();
            return 1;
        }
    }
}
