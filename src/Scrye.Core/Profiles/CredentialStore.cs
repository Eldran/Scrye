using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Scrye.Core.Profiles;

/// <summary>
/// Stores login secrets in the OS credential store so passwords never live in
/// profile JSON — a layer holds only a <see cref="ProfileLayer.PasswordRef"/> key
/// into this store.
///
/// <list type="bullet">
/// <item><b>Windows</b>: Credential Manager (advapi32 CredRead/Write/Delete, P/Invoke, no NuGet).</item>
/// <item><b>Linux</b>: the Secret Service — GNOME Keyring or KWallet — through the
/// <c>secret-tool</c> CLI from <c>libsecret-tools</c>.</item>
/// <item><b>macOS</b>: not backed yet; <see cref="Available"/> is false and everything no-ops.</item>
/// </list>
///
/// <para><b>Why a CLI on Linux rather than P/Invoke, when Windows gets P/Invoke?</b> libsecret's
/// store/lookup functions are C VARIADIC (a schema pointer, then NULL-terminated attribute
/// pairs). Declaring those from C# means hand-matching a platform calling convention with no
/// compiler check, and getting it subtly wrong corrupts memory rather than failing cleanly —
/// a poor trade for code that handles passwords. <c>secret-tool</c> is the same Secret Service
/// API with a contract that cannot be marshalled wrongly.</para>
///
/// <para>The secret is written to the child's <b>stdin</b>, never passed as an argument, so it
/// does not appear in <c>ps</c> output or a shell history. (This is exactly why the macOS
/// <c>security</c> CLI is NOT an acceptable shortcut there: it takes the password in argv.)</para>
/// </summary>
public static class CredentialStore
{
    // Attribute pair every entry is filed under, so lookup and clear can find what store wrote.
    private const string AttrService = "service";
    private const string AttrServiceValue = "scrye";
    private const string AttrAccount = "account";

    private static bool? _available;

    /// <summary>True when this machine can actually store a secret. On Linux that means the
    /// <c>secret-tool</c> binary exists; probed once, because the answer cannot change while
    /// the app runs and the probe spawns a process.</summary>
    public static bool Available => _available ??= Probe();

    private static bool Probe()
    {
        if (OperatingSystem.IsWindows()) return true;
        if (!OperatingSystem.IsLinux()) return false;      // macOS: see the class remarks
        return OnPath("secret-tool");
    }

    /// <summary>Is this executable on PATH? Deliberately a file-system look rather than running
    /// the binary: secret-tool has no --version and answers BOTH --version and --help with a
    /// usage message and exit code 2, so probing by exit status reports "missing" on every
    /// machine that actually has it. Not running it also means no D-Bus contact and no chance
    /// of a "unlock your keyring" prompt at startup.</summary>
    private static bool OnPath(string exe)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;
        foreach (string dir in path.Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            try { if (File.Exists(Path.Combine(dir, exe))) return true; }
            catch { /* a malformed PATH entry must not break the probe */ }
        }
        return false;
    }

    /// <summary>Why saving a password is unavailable, phrased for the user, or null when it is
    /// available. The UI needs this: silently not saving a password the user typed is the worst
    /// of the options.</summary>
    public static string? UnavailableReason =>
        Available ? null
        : OperatingSystem.IsLinux()
            ? "password saving needs the 'secret-tool' command - install it with: sudo apt install libsecret-tools"
            : OperatingSystem.IsMacOS()
                ? "saving passwords in the macOS Keychain is not implemented yet"
                : "no supported credential store on this platform";

    /// <summary>Save (or overwrite) a secret under <paramref name="key"/>. Returns whether it
    /// actually stored.
    ///
    /// <para>The result matters: <see cref="Available"/> only says a credential store EXISTS,
    /// not that it will accept a write. A Linux box with secret-tool installed but no unlocked
    /// keyring — a fresh VM, an SSH session, a headless run — fails here. A caller that ignored
    /// this and recorded the PasswordRef anyway would leave a profile pointing at a secret that
    /// was never written, and auto-login would fail later with nothing to explain it.</para></summary>
    public static bool Save(string key, string secret)
    {
        if (!Available || string.IsNullOrEmpty(key)) return false;
        if (!OperatingSystem.IsWindows())
            return Run(new[] { "store", "--label=Scrye: " + key, AttrService, AttrServiceValue, AttrAccount, key },
                       secret, out _);
        byte[] blob = System.Text.Encoding.Unicode.GetBytes(secret);
        IntPtr blobPtr = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new CREDENTIALW
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = key,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = "scrye",
            };
            return CredWriteW(ref cred, 0);
        }
        finally { Marshal.FreeHGlobal(blobPtr); }
    }

    /// <summary>The secret stored under <paramref name="key"/>, or null if absent/unavailable.</summary>
    public static string? Load(string key)
    {
        if (!Available || string.IsNullOrEmpty(key)) return null;
        if (!OperatingSystem.IsWindows())
        {
            if (!Run(new[] { "lookup", AttrService, AttrServiceValue, AttrAccount, key }, null, out string outp))
                return null;                       // no such entry, or the keyring is locked
            // Returned VERBATIM. secret-tool adds no trailing newline of its own (verified
            // against a live gnome-keyring), so trimming one here would silently corrupt any
            // password that legitimately ends in a newline or a space.
            return outp;
        }
        if (!CredReadW(key, CRED_TYPE_GENERIC, 0, out IntPtr credPtr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIALW>(credPtr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return "";
            byte[] blob = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);
            return System.Text.Encoding.Unicode.GetString(blob);
        }
        finally { CredFree(credPtr); }
    }

    public static void Delete(string key)
    {
        if (!Available || string.IsNullOrEmpty(key)) return;
        if (!OperatingSystem.IsWindows())
        {
            Run(new[] { "clear", AttrService, AttrServiceValue, AttrAccount, key }, null, out _);
            return;
        }
        CredDeleteW(key, CRED_TYPE_GENERIC, 0);
    }

    // ---- secret-tool (Linux Secret Service) ----------------------------------

    /// <summary>Run secret-tool with the given arguments, optionally writing
    /// <paramref name="stdin"/> to it, and return whether it exited 0. Arguments go through
    /// ArgumentList so a key containing spaces or quotes cannot be reinterpreted, and no shell
    /// is involved at any point. Never throws: a missing binary, a locked keyring and a machine
    /// with no D-Bus session all just come back false.</summary>
    private static bool Run(string[] args, string? stdin, out string stdout)
    {
        stdout = "";
        try
        {
            var psi = new ProcessStartInfo("secret-tool")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = stdin is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using Process? p = Process.Start(psi);
            if (p is null) return false;

            if (stdin is not null)
            {
                p.StandardInput.Write(stdin);       // Write, not WriteLine: no stray newline in the secret
                p.StandardInput.Close();
            }
            stdout = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();            // drained so a chatty failure cannot deadlock the pipe
            // A keyring prompt can block; without one this returns immediately. The cap stops a
            // headless or broken session hanging the profile save forever.
            if (!p.WaitForExit(15_000)) { try { p.Kill(entireProcessTree: true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }                     // not installed, or refused to launch
    }

    // ---- advapi32 ------------------------------------------------------------

    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIALW
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref CREDENTIALW credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
