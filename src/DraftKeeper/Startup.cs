using Microsoft.Win32;

namespace DraftKeeper;

/// <summary>
/// Starting with Windows, through the per-user run key. No service, no scheduled task
/// and no elevation. Without it, whatever is typed after a crash and before someone
/// starts the program is not saved.
/// </summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DraftKeeper";

    private static string Command
    {
        get
        {
            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            return $"\"{exe}\"";
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>The path recorded, which is not always the path now running.</summary>
    public static string? RegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }
        catch { return null; }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return false;

            if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Rewrites the recorded path when the program has moved. An entry pointing at a
    /// missing executable fails silently at every boot.
    /// </summary>
    public static void Reconcile(bool wanted)
    {
        if (!wanted)
        {
            if (IsEnabled()) Set(false);
            return;
        }

        var recorded = RegisteredCommand();
        if (recorded is null || !string.Equals(recorded, Command, StringComparison.OrdinalIgnoreCase))
            Set(true);
    }
}
