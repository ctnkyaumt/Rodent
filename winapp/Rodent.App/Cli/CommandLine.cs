using Rodent.Core.Diagnostics;

namespace Rodent.App.Cli;

/// <summary>What the user asked for on the command line.</summary>
public sealed class CliOptions
{
    public bool Help;
    public bool Version;
    public bool ListDevices;
    public bool ShowLogPath;

    /// <summary>Start hidden in the tray (used by the run-at-boot entry).</summary>
    public bool Tray;

    // logging
    public LogLevel LogLevel = LogLevel.Info;
    public string? LogFile;
    public bool Console;           // mirror the log to the terminal

    public List<string> Errors { get; } = new();
    public bool WantsConsoleOnly => Help || Version || ListDevices || ShowLogPath;

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string raw = args[i];
            if (raw.Length == 0) continue;

            // Accept --name=value as well as --name value.
            string name = raw, inline = "";
            int eq = raw.IndexOf('=');
            if (eq > 0) { name = raw[..eq]; inline = raw[(eq + 1)..]; }
            bool HasInline() => inline.Length > 0;
            string? Value()
            {
                if (HasInline()) return inline;
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) return args[++i];
                return null;
            }

            switch (name.ToLowerInvariant())
            {
                case "-h": case "-?": case "/?": case "--help": o.Help = true; break;
                case "-v": case "--version": o.Version = true; break;
                case "-l": case "--list": case "--list-devices": o.ListDevices = true; break;
                case "--log-path": o.ShowLogPath = true; break;

                case "--tray": case "--minimized": o.Tray = true; break;

                case "--log":
                {
                    string? v = Value();
                    if (v == null) o.LogLevel = LogLevel.Debug;             // bare --log = be chatty
                    else if (Log.TryParseLevel(v, out var lvl)) o.LogLevel = lvl;
                    else o.Errors.Add($"unknown log level '{v}' (off|error|warn|info|debug|trace)");
                    break;
                }
                case "--no-log": o.LogLevel = LogLevel.Off; break;
                case "--log-file":
                {
                    string? v = Value();
                    if (v == null) o.Errors.Add("--log-file needs a path");
                    else o.LogFile = v;
                    break;
                }
                case "--verbose":
                    o.LogLevel = LogLevel.Debug;
                    o.Console = true;
                    break;
                case "--console":
                    o.Console = true;
                    break;

                default:
                    o.Errors.Add($"unknown option '{raw}'");
                    break;
            }
        }
        return o;
    }

    public const string HelpText = """
        Rodent - mouse configurator (Logitech HID++, plus other brands)

        USAGE
          Rodent.exe [command] [options]

        COMMANDS
          (none)               Launch the app
          --list, -l           Print detected devices and exit
          --log-path           Print the log file location and exit
          --version, -v        Print the version and exit
          --help, -h           Show this help

        RUN OPTIONS
          --tray               Start hidden in the notification area

        LOGGING
          --log <level>        off|error|warn|info|debug|trace  (default: info)
          --log <no value>     Same as --log debug
          --no-log             Disable logging entirely
          --log-file <path>    Write the log somewhere else
          --console            Mirror log output to the terminal
          --verbose            Same as --log debug --console

        EXAMPLES
          Rodent.exe --verbose            # troubleshoot device detection
          Rodent.exe --list
        """;
}
