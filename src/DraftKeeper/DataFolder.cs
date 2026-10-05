using System.IO;

namespace DraftKeeper;

// Everything lives in %LOCALAPPDATA%\DraftKeeper, or in DRAFTKEEPER_DATA if that's set.
// The tests use that to run their own copy without touching your drafts.
internal static class DataFolder
{
    private static readonly string? Chosen = Environment.GetEnvironmentVariable("DRAFTKEEPER_DATA");

    public static string Path { get; } = string.IsNullOrWhiteSpace(Chosen)
        ? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DraftKeeper")
        : System.IO.Path.GetFullPath(Chosen);

    // A copy on its own folder leaves the startup entry alone.
    public static bool IsDefault => string.IsNullOrWhiteSpace(Chosen);

    // One running copy per folder.
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
