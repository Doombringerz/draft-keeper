using System.IO;
using System.Diagnostics;

namespace DraftKeeper;

internal static class Program
{
    private static DraftStore? _store;
    private static ImageStore? _images;
    private static Settings _settings = new();
    private static InputWatcher? _watcher;
    private static DraftsForm? _form;
    private static NotifyIcon? _tray;

    [STAThread]
    private static void Main(string[] args)
    {
        using var single = new Mutex(true, DataFolder.InstanceName, out var isFirst);
        if (!isFirst) return;

        // --show opens the window at start as well as the tray icon.
        var openWindow = args.Any(a => a.Equals("--show", StringComparison.OrdinalIgnoreCase));

        ApplicationConfiguration.Initialize();

        _settings = Settings.Load();
        Theme.Apply(_settings.Appearance);

        // Retention goes in through the constructors. See DraftStore.
        var retentionWindow = TimeSpan.FromHours(_settings.RetentionHours);
        _store = new DraftStore(retentionWindow);
        _images = new ImageStore(retentionWindow) { Enabled = _settings.SaveClipboardImages };
        _watcher = new InputWatcher(_store);
        _form = new DraftsForm(_store, _images, _settings);

        _watcher.Activity += session => _images.CurrentSession = session;

        var menu = new ContextMenuStrip();

        var open = new ToolStripMenuItem("Show drafts", null, (_, _) => ShowWindow());
        var pause = new ToolStripMenuItem("Pause capture") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) =>
        {
            _watcher.Paused = pause.Checked;
            UpdateTip();
        };

        // Off until asked for. Nothing looks at the clipboard while it is unchecked.
        var clipboard = new ToolStripMenuItem("Save clipboard images")
        {
            CheckOnClick = true,
            Checked = _settings.SaveClipboardImages
        };
        clipboard.CheckedChanged += (_, _) =>
        {
            _images.Enabled = clipboard.Checked;
            _settings.SaveClipboardImages = clipboard.Checked;
            _settings.Save();
            _form?.Reload();
            UpdateTip();
        };

        if (DataFolder.IsDefault) Startup.Reconcile(_settings.StartWithWindows);
        _settings.Save();

        var startWithWindows = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = Startup.IsEnabled()
        };
        startWithWindows.CheckedChanged += (_, _) =>
        {
            var ok = Startup.Set(startWithWindows.Checked);
            if (!ok)
            {
                MessageBox.Show(
                    "Could not change the Windows startup entry. The registry key could not be written.",
                    "Draft Keeper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                startWithWindows.Checked = Startup.IsEnabled();
                return;
            }
            _settings.StartWithWindows = startWithWindows.Checked;
            _settings.Save();
        };

        var appearance = new ToolStripMenuItem("Appearance");
        foreach (var mode in new[] { Appearance.System, Appearance.Dark, Appearance.Light })
        {
            var choice = mode;
            var item = new ToolStripMenuItem(mode.ToString()) { Checked = _settings.Appearance == mode, Tag = mode };
            item.Click += (_, _) =>
            {
                foreach (ToolStripMenuItem other in appearance.DropDownItems) other.Checked = false;
                item.Checked = true;
                _settings.AppearanceMode = choice.ToString();
                _settings.Save();
                Theme.Apply(choice);
                RebuildWindow();
            };
            appearance.DropDownItems.Add(item);
        }

        var retention = new ToolStripMenuItem("Keep drafts for");
        foreach (var hours in new[] { 1, 6, 12, 24, 72, 168 })
        {
            var label = hours < 24
                ? $"{hours} hour{(hours == 1 ? "" : "s")}"
                : $"{hours / 24} day{(hours == 24 ? "" : "s")}";
            var item = new ToolStripMenuItem(label)
            {
                Checked = hours == _settings.RetentionHours,
                Tag = hours
            };
            item.Click += (s, _) =>
            {
                foreach (ToolStripMenuItem other in retention.DropDownItems) other.Checked = false;
                item.Checked = true;
                _store.Retention = TimeSpan.FromHours((int)item.Tag!);
                _settings.RetentionHours = (int)item.Tag!;
                _settings.Save();
                if (_images is not null) _images.Retention = TimeSpan.FromHours((int)item.Tag!);
            };
            retention.DropDownItems.Add(item);
        }

        var folder = new ToolStripMenuItem("Open data folder", null, (_, _) =>
        {
            var dir = Path.GetDirectoryName(_store.FilePath);
            if (dir is not null) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        });

        var wipe = new ToolStripMenuItem("Delete everything saved", null, (_, _) =>
        {
            var answer = MessageBox.Show(
                "Delete every saved draft and image now?", "Draft Keeper",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes)
            {
                _store.Clear();
                _images?.Clear();
                _form?.Reload();
            }
        });

        var quit = new ToolStripMenuItem("Quit", null, (_, _) =>
        {
            _tray!.Visible = false;
            Application.Exit();
        });

        menu.Items.AddRange(new ToolStripItem[]
        {
            open, new ToolStripSeparator(), pause, clipboard, retention, appearance,
            startWithWindows,
            new ToolStripSeparator(), folder, wipe,
            new ToolStripSeparator(), quit
        });

        _tray = new NotifyIcon
        {
            Icon = Brand.Icon,
            Visible = true,
            ContextMenuStrip = menu,
            Text = "Draft Keeper"
        };
        _tray.DoubleClick += (_, _) => ShowWindow();


        UpdateTip();
        var tip = new System.Windows.Forms.Timer { Interval = 5000 };
        tip.Tick += (_, _) => UpdateTip();
        tip.Start();

        // Clipboard access has to happen on this thread. Poll() returns immediately
        // while the feature is switched off.
        var clip = new System.Windows.Forms.Timer { Interval = 800 };
        clip.Tick += (_, _) => _images?.Poll();
        clip.Start();

        _images.Changed += () => { if (_form is { Visible: true }) _form.Reload(); };

        Application.ApplicationExit += (_, _) =>
        {
            _watcher?.Dispose();
            _tray?.Dispose();
        };

        if (openWindow) ShowWindow();

        Application.Run();
    }

    private static void UpdateTip()
    {
        if (_tray is null || _watcher is null) return;
        _tray.Text = _watcher.Paused
            ? "Draft Keeper - paused"
            : $"Draft Keeper - watching {_watcher.WatchedCount} box(es)";
    }

    /// <summary>
    /// Rebuilds the window in the new palette. Controls take their colours when built.
    /// </summary>
    private static void RebuildWindow()
    {
        if (_store is null || _images is null) return;
        var wasOpen = _form is { Visible: true };
        var old = _form;
        _form = new DraftsForm(_store, _images, _settings);
        old?.Dispose();
        if (wasOpen) ShowWindow();
    }

    private static void ShowWindow()
    {
        if (_form is null) return;
        _form.Reload();
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.BringToFront();
        _form.Activate();
    }

}
