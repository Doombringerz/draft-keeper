using System.Runtime.InteropServices;

namespace DraftKeeper;

// Windows' own encryption, tied to your account. Other accounts can't read it, and a
// copy on another PC is useless.
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const int CryptprotectUiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob input, string? description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private static DataBlob ToBlob(byte[] data)
    {
        var blob = new DataBlob { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, blob.pbData, data.Length);
        return blob;
    }

    private static byte[] FromBlob(DataBlob blob)
    {
        var result = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, result, 0, blob.cbData);
        return result;
    }

    public static byte[] Protect(byte[] plain)
    {
        var input = ToBlob(plain);
        try
        {
            if (!CryptProtectData(ref input, "Draft Keeper", IntPtr.Zero, IntPtr.Zero,
                                  IntPtr.Zero, CryptprotectUiForbidden, out var output))
                throw new InvalidOperationException("Encryption failed.");
            try { return FromBlob(output); }
            finally { LocalFree(output.pbData); }
        }
        finally { Marshal.FreeHGlobal(input.pbData); }
    }

    public static byte[] Unprotect(byte[] cipher)
    {
        var input = ToBlob(cipher);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                    IntPtr.Zero, CryptprotectUiForbidden, out var output))
                throw new InvalidOperationException("Decryption failed.");
            try { return FromBlob(output); }
            finally { LocalFree(output.pbData); }
        }
        finally { Marshal.FreeHGlobal(input.pbData); }
    }
}
