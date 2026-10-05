using System.Runtime.InteropServices;

namespace DraftKeeper;

public enum Appearance
{
    System,
    Dark,
    Light
}

/// <summary>
/// One palette for the whole window. Windows Forms paints most surfaces light grey
/// whatever the system is set to. Every colour is set here. Scrollbars and list headers
/// are drawn by the operating system and are themed through <see cref="ApplyWindowTheme"/>.
/// </summary>
internal static class Theme
{
    public static Appearance Mode { get; private set; } = Appearance.System;
    public static bool IsDark { get; private set; } = true;

    public static Color Window { get; private set; }
    public static Color Surface { get; private set; }
    public static Color Raised { get; private set; }
    public static Color Line { get; private set; }
    public static Color Text { get; private set; }
    public static Color Muted { get; private set; }
    public static Color Faint { get; private set; }
    public static Color Accent { get; private set; }
    public static Color Live { get; private set; }
    public static Color Selection { get; private set; }

    public static readonly Font Ui = new("Segoe UI", 9f);
    public static readonly Font UiBold = new("Segoe UI Semibold", 9f);
    public static readonly Font Small = new("Segoe UI", 8f);
    public static readonly Font Title = new("Segoe UI Semibold", 12f);

    static Theme() => Apply(Appearance.System);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr window, string? app, string? id);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private const int UseImmersiveDarkMode = 20;

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch { return true; }
    }

    public static void Apply(Appearance mode)
    {
        Mode = mode;
        IsDark = mode switch
        {
            Appearance.Dark => true,
            Appearance.Light => false,
            _ => SystemPrefersDark()
        };

        if (IsDark)
        {
            Window = Color.FromArgb(24, 24, 27);
            Surface = Color.FromArgb(32, 32, 36);
            Raised = Color.FromArgb(41, 41, 46);
            Line = Color.FromArgb(58, 58, 64);
            Text = Color.FromArgb(228, 228, 231);
            Muted = Color.FromArgb(140, 140, 148);
            Faint = Color.FromArgb(96, 96, 104);
            Accent = Color.FromArgb(217, 119, 87);
            Live = Color.FromArgb(94, 196, 168);
            Selection = Color.FromArgb(52, 54, 62);
            SetFrameworkColorMode();
        }
        else
        {
            Window = Color.FromArgb(247, 247, 248);
            Surface = Color.FromArgb(255, 255, 255);
            Raised = Color.FromArgb(240, 240, 242);
            Line = Color.FromArgb(214, 214, 219);
            Text = Color.FromArgb(28, 28, 32);
            Muted = Color.FromArgb(96, 96, 104);
            Faint = Color.FromArgb(138, 138, 146);
            Accent = Color.FromArgb(198, 98, 64);
            Live = Color.FromArgb(22, 138, 114);
            Selection = Color.FromArgb(232, 234, 240);
            SetFrameworkColorMode();
        }
    }

    /// <summary>
    /// Scrollbars inside a text box are drawn by Windows and stay light however the
    /// control is painted. The framework's colour mode reaches them.
    /// </summary>
    private static void SetFrameworkColorMode()
    {
        try
        {
#pragma warning disable WFO5001
            Application.SetColorMode(IsDark ? SystemColorMode.Dark : SystemColorMode.Classic);
#pragma warning restore WFO5001
        }
        catch { /* older framework: the rest of the palette still applies */ }
    }

    /// <summary>
    /// Scrollbars, list headers and the window frame are drawn by the operating system.
    /// Setting the control's theme is the only way to stop them coming back light.
    /// </summary>
    public static void ApplyWindowTheme(Control control)
    {
        try
        {
            if (!control.IsHandleCreated) return;
            SetWindowTheme(control.Handle, IsDark ? "DarkMode_Explorer" : "Explorer", null);
        }
        catch { /* an older build of Windows keeps the default */ }
    }

    public static void ApplyTitleBar(Form form)
    {
        try
        {
            if (!form.IsHandleCreated) return;
            var on = IsDark ? 1 : 0;
            DwmSetWindowAttribute(form.Handle, UseImmersiveDarkMode, ref on, sizeof(int));
        }
        catch { /* older Windows keeps the default frame */ }
    }

    public static Font Monospace()
    {
        foreach (var name in new[] { "Cascadia Mono", "Consolas", "Lucida Console" })
        {
            try
            {
                using var probe = new Font(name, 9.5f);
                if (probe.Name == name) return new Font(name, 9.5f);
            }
            catch { /* try the next one */ }
        }
        return new Font(FontFamily.GenericMonospace, 9.5f);
    }

    public static void StyleButton(Button b, bool primary = false)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = primary ? Accent : Line;
        b.FlatAppearance.MouseOverBackColor = primary
            ? ControlPaint.Light(Accent, 0.1f)
            : Raised;
        b.FlatAppearance.MouseDownBackColor = primary
            ? ControlPaint.Dark(Accent, 0.05f)
            : Surface;
        b.BackColor = primary ? Accent : Surface;
        b.ForeColor = primary ? Color.White : Text;
        b.Font = Ui;
        b.Height = 30;
        b.Cursor = Cursors.Hand;
        b.UseVisualStyleBackColor = false;
    }
}
