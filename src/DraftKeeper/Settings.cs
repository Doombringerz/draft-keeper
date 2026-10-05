using System.IO;
using System.Text.Json;

namespace DraftKeeper;

// Preferences, plain text next to the drafts. Colours, how long to keep things, window
// layout. Never anything you typed.
public sealed class Settings
{
    public string AppearanceMode { get; set; } = nameof(Appearance.System);
    public int RetentionHours { get; set; } = 24;
    public bool SaveClipboardImages { get; set; }

    // On by default. Not running after a crash is the one time it can't afford to be off.
    public bool StartWithWindows { get; set; } = true;

    // Where you last dragged the divider.
    public int ListHeight { get; set; } = 258;

    private static string Path => DataFolder.File("settings.json");

    public Appearance Appearance =>
        Enum.TryParse<Appearance>(AppearanceMode, ignoreCase: true, out var mode)
            ? mode
            : DraftKeeper.Appearance.System;

    public static Settings Load()
    {
        try
        {
            if (!File.Exists(Path)) return new Settings();
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path)) ?? new Settings();
        }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(Path,JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not worth crashing over a setting */ }
    }
}
