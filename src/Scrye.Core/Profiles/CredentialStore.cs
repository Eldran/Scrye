using System.Runtime.InteropServices;

namespace Scrye.Core.Profiles;

/// <summary>
/// Stores login secrets in the OS credential store so passwords never live in
/// profile JSON — a layer holds only a <see cref="ProfileLayer.PasswordRef"/> key
/// into this store. Windows: Credential Manager (advapi32 CredRead/Write/Delete,
/// P/Invoke — no NuGet). Other platforms: not yet backed (returns null / no-ops);
/// macOS Keychain / libsecret are future work.
/// </summary>
public static class CredentialStore
{
    public static bool Available => OperatingSystem.IsWindows();

    /// <summary>Save (or overwrite) a secret under <paramref name="key"/>. No-op when unavailable.</summary>
    public static void Save(string key, string secret)
    {
        if (!Available || string.IsNullOrEmpty(key)) return;
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
            CredWriteW(ref cred, 0);
        }
        finally { Marshal.FreeHGlobal(blobPtr); }
    }

    /// <summary>The secret stored under <paramref name="key"/>, or null if absent/unavailable.</summary>
    public static string? Load(string key)
    {
        if (!Available || string.IsNullOrEmpty(key)) return null;
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
        CredDeleteW(key, CRED_TYPE_GENERIC, 0);
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
