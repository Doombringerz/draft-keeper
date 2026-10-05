using System.IO;
using System.Text.Json;

namespace DraftKeeper;

/// <summary>
/// Preferences, kept in plain text next to the store. They hold colours, retention and
/// window layout, never typed text.
/// </summary>
public sealed class Settings
{
    public string AppearanceMode { get; set; } = nameof(Appearance.System);
    public int RetentionHours { get; set; } = 24;
    public bool SaveClipboardImages { get; set; }

    /// <summary>
    /// On by default. After a crash nothing is saved until the program runs again.
    /// </summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>Height of the draft list: where the divider was last dragged to.</summary>
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
        catch { /* a preference that will not save is not worth a crash */ }
    }
}
