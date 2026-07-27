using System.Runtime.InteropServices;

namespace Rodent.Core.Automation;

/// <summary>
/// Whether Rodent can act on the app in front of it. Windows refuses a
/// medium-integrity process both the keyboard events destined for an elevated
/// window and any SendInput aimed at it, so every per-app binding silently does
/// nothing while such an app has focus (lighting still follows, because reading
/// the foreground window needs no rights) — the "assignments don't work in the
/// game, but the LEDs change" symptom.
/// </summary>
public static class Elevation
{
    /// <summary>True when this process is running elevated (as administrator).</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// True when the process runs at a higher integrity level than we do — the
    /// case Windows won't let us hook or inject into. Its token being unreadable
    /// counts as higher: that denial IS the integrity check. Cheap enough for the
    /// foreground watcher (one open per app switch).
    /// </summary>
    public static bool ProcessOutOfReach(int pid)
    {
        if (pid <= 4) return false;                        // Idle/System are never the foreground app
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return true;
        try
        {
            if (!OpenProcessToken(h, TOKEN_QUERY, out IntPtr token)) return true;
            try
            {
                int theirs = IntegrityOf(token);
                int ours = OwnIntegrity();
                return theirs > ours;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(h); }
    }

    private static int OwnIntegrity()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return IntegrityOf(id.Token);
        }
        catch { return SECURITY_MANDATORY_MEDIUM; }
    }

    /// <summary>Integrity level of a token (the last sub-authority of its label SID).</summary>
    private static int IntegrityOf(IntPtr token)
    {
        GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int len);
        if (len <= 0) return SECURITY_MANDATORY_MEDIUM;
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buf, len, out _))
                return SECURITY_MANDATORY_MEDIUM;
            IntPtr sid = Marshal.ReadIntPtr(buf);          // TOKEN_MANDATORY_LABEL.Label.Sid
            if (sid == IntPtr.Zero) return SECURITY_MANDATORY_MEDIUM;
            int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            return count <= 0 ? SECURITY_MANDATORY_MEDIUM : Marshal.ReadInt32(GetSidSubAuthority(sid, count - 1));
        }
        catch { return SECURITY_MANDATORY_MEDIUM; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// Relaunch this exe elevated (UAC prompt) and report whether the new process
    /// started — the caller shuts itself down on success.
    /// </summary>
    public static bool RestartElevated(string? arguments = null)
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0) return false;
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",                       // triggers the UAC prompt
                Arguments = arguments ?? "",
            };
            return System.Diagnostics.Process.Start(psi) != null;
        }
        catch { return false; }                        // user declined the prompt
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenIntegrityLevel = 25;            // TOKEN_INFORMATION_CLASS
    private const int SECURITY_MANDATORY_MEDIUM = 0x2000;  // High = 0x3000, System = 0x4000

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, int index);
}
