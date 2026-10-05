using Microsoft.Win32;

namespace DraftKeeper;

// Starts with Windows through your own Run key. No service, no admin.
// Otherwise nothing gets saved after a crash until you remember to start it.
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

    // Can point somewhere else than the exe that's running now.
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

    // Moved the exe? Fix the entry. One pointing at nothing just fails quietly every boot.
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
