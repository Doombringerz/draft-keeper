using System.IO;

namespace DraftKeeper;

/// <summary>
/// Where drafts, images and settings are kept: %LOCALAPPDATA%\DraftKeeper, or the folder
/// named in DRAFTKEEPER_DATA. The tests set it to run a second copy next to the one in
/// daily use without touching its drafts.
/// </summary>
internal static class DataFolder
{
    private static readonly string? Chosen = Environment.GetEnvironmentVariable("DRAFTKEEPER_DATA");

    public static string Path { get; } = string.IsNullOrWhiteSpace(Chosen)
        ? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DraftKeeper")
        : System.IO.Path.GetFullPath(Chosen);

    /// <summary>A copy running on its own folder is a side copy. It leaves the startup entry alone.</summary>
    public static bool IsDefault => string.IsNullOrWhiteSpace(Chosen);

    /// <summary>One running copy per folder.</summary>
    public static string InstanceName => IsDefault
        ? "DraftKeeper.SingleInstance"
        : "DraftKeeper.SingleInstance." + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.ToUpperInvariant())))[..16];

    public static string File(string name)
    {
        Directory.CreateDirectory(Path);
        return System.IO.Path.Combine(Path, name);
    }
}
