using System.ComponentModel;

namespace DraftKeeper;

public sealed class DraftsForm : Form
{
    private readonly DraftStore _store;
    private readonly ImageStore _images;
    private readonly System.Windows.Forms.Timer _refresh;

    private readonly Label _heading = new();
    private readonly Label _subheading = new();
    private readonly TextBox _search = new();

    private readonly ListView _list = new();
    private readonly TextBox _preview = new();
    private readonly Label _previewMeta = new();

    private readonly ListView _imageList = new();
    private readonly PictureBox _imagePreview = new();
    private readonly Label _imageHint = new();

    private readonly Panel _tabStrip = new();
    private readonly Panel _pageHost = new();
    private Panel _draftsPage = null!;
    private Panel _imagesPage = null!;
    private int _activeTab;
    private string _draftsTabText = "Drafts";
    private string _imagesTabText = "Images";

    private readonly Settings _settings;

    public DraftsForm(DraftStore store, ImageStore images, Settings settings)
    {
        _store = store;
        _images = images;
        _settings = settings;

        Text = "Draft Keeper";
        Icon = Brand.Icon;
        ClientSize = new Size(1040, 660);
        MinimumSize = new Size(760, 480);
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = true;
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        Font = Theme.Ui;

        Controls.Add(BuildBody());
        Controls.Add(BuildHeader());

        _refresh = new System.Windows.Forms.Timer { Interval = 1500 };
        _refresh.Tick += (_, _) => { if (Visible) Reload(); };
        _refresh.Start();

        Reload();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
        // Scrollbars are drawn by Windows and stay light until the control gets a theme.
        foreach (var control in new Control[] { _list, _imageList, _preview })
            Theme.ApplyWindowTheme(control);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // The frame only takes the setting once the window is on screen.
        Theme.ApplyTitleBar(this);
        FillLastColumn(_list);
        FillLastColumn(_imageList);
        Reload();
        _list.Invalidate();
    }

    private Control BuildHeader()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Theme.Surface, Padding = new Padding(18, 0, 18, 0) };
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Line);
            e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
        };

        var mark = new Panel { Size = new Size(30, 30), Location = new Point(18, 19), BackColor = Color.Transparent };
        mark.Paint += (_, e) => DrawMark(e.Graphics, new Rectangle(0, 0, 30, 30));

        _heading.Text = "Draft Keeper";
        _heading.Font = Theme.Title;
        _heading.ForeColor = Theme.Text;
        _heading.AutoSize = true;
        _heading.Location = new Point(58, 15);

        _subheading.Font = Theme.Small;
        _subheading.ForeColor = Theme.Muted;
        _subheading.AutoSize = true;
        _subheading.Location = new Point(60, 38);

        var searchHost = new Panel
        {
            Size = new Size(260, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Theme.Raised,
            Padding = new Padding(9, 5, 9, 5)
        };
        searchHost.Location = new Point(bar.Width - 278, 20);
        searchHost.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Line);
            e.Graphics.DrawRectangle(pen, 0, 0, searchHost.Width - 1, searchHost.Height - 1);
        };

        _search.BorderStyle = BorderStyle.None;
        _search.BackColor = Theme.Raised;
        _search.ForeColor = Theme.Text;
        _search.Dock = DockStyle.Fill;
        _search.Font = Theme.Ui;
        _search.PlaceholderText = "Filter by session or text";
        _search.TextChanged += (_, _) => Reload();
        searchHost.Controls.Add(_search);

        bar.Controls.Add(mark);
        bar.Controls.Add(_heading);
        bar.Controls.Add(_subheading);
        bar.Controls.Add(searchHost);
        return bar;
    }

    private static void DrawMark(Graphics g, Rectangle box)
    {
        if (Brand.Mark is { } mark)
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(mark, box);
            return;
        }

        DrawFallbackMark(g, box);
    }

    /// <summary>Used only if the embedded mark cannot be loaded.</summary>
    private static void DrawFallbackMark(Graphics g, Rectangle box)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var w = box.Width;
        var h = box.Height;
        var fold = w * 0.34f;

        using var page = new System.Drawing.Drawing2D.GraphicsPath();
        page.AddLines(new[]
        {
            new PointF(w * 0.14f, h * 0.06f),
            new PointF(w * 0.66f, h * 0.06f),
            new PointF(w * 0.86f, h * 0.06f + fold),
            new PointF(w * 0.86f, h * 0.94f),
            new PointF(w * 0.14f, h * 0.94f)
        });
        page.CloseFigure();

        using var fill = new SolidBrush(Theme.Text);
        g.FillPath(fill, page);

        using var corner = new SolidBrush(Theme.Surface);
        g.FillPolygon(corner, new[]
        {
            new PointF(w * 0.66f, h * 0.06f),
            new PointF(w * 0.86f, h * 0.06f + fold),
            new PointF(w * 0.66f, h * 0.06f + fold)
        });

        using var rule = new SolidBrush(Theme.Surface);
        for (var i = 0; i < 3; i++)
            g.FillRectangle(rule, w * 0.26f, h * (0.45f + i * 0.15f), w * (i == 2 ? 0.24f : 0.4f), h * 0.07f);

        using var dot = new SolidBrush(Theme.Accent);
        g.FillEllipse(dot, w * 0.62f, h * 0.60f, w * 0.30f, h * 0.30f);
    }

    /// <summary>
    /// Tabs drawn by hand. A TabControl paints the strip behind its tabs itself, in its
    /// own light grey, and offers no way to change it.
    /// </summary>
    private Control BuildBody()
    {
        _tabStrip.Dock = DockStyle.Top;
        _tabStrip.Height = 34;
        _tabStrip.BackColor = Theme.Window;
        _tabStrip.Paint += DrawTabs;
        _tabStrip.MouseUp += (_, e) =>
        {
            var index = e.X / TabWidth;
            if (index is 0 or 1 && index != _activeTab) { _activeTab = index; ShowActiveTab(); }
        };
        _tabStrip.MouseMove += (_, _) => _tabStrip.Cursor = Cursors.Hand;

        _pageHost.Dock = DockStyle.Fill;
        _pageHost.BackColor = Theme.Window;

        _draftsPage = BuildDraftsPage();
        _imagesPage = BuildImagesPage();
        _pageHost.Controls.Add(_draftsPage);
        _pageHost.Controls.Add(_imagesPage);
        ShowActiveTab();

        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window, Padding = new Padding(12, 8, 12, 12) };
        host.Controls.Add(_pageHost);
        host.Controls.Add(_tabStrip);
        return host;
    }

    private const int TabWidth = 128;

    private void ShowActiveTab()
    {
        _draftsPage.Visible = _activeTab == 0;
        _imagesPage.Visible = _activeTab == 1;
        _tabStrip.Invalidate();
    }

    private void DrawTabs(object? sender, PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Window);
        var labels = new[] { _draftsTabText, _imagesTabText };
        using var line = new Pen(Theme.Line);
        e.Graphics.DrawLine(line, 0, _tabStrip.Height - 1, _tabStrip.Width, _tabStrip.Height - 1);

        for (var i = 0; i < labels.Length; i++)
        {
            var bounds = new Rectangle(i * TabWidth, 0, TabWidth, _tabStrip.Height);
            var selected = i == _activeTab;
            using var text = new SolidBrush(selected ? Theme.Text : Theme.Muted);
            e.Graphics.DrawString(labels[i], selected ? Theme.UiBold : Theme.Ui, text, bounds,
                new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            if (!selected) continue;
            using var underline = new SolidBrush(Theme.Accent);
            e.Graphics.FillRectangle(underline, bounds.Left + 18, bounds.Bottom - 2, bounds.Width - 36, 2);
        }
    }

    private Panel BuildDraftsPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window, Padding = new Padding(0, 10, 0, 0) };

        StyleList(_list);
        _list.Columns.Add("Session", 190);
        _list.Columns.Add("Saved", 110);
        _list.Columns.Add("", 62);
        _list.Columns.Add("Draft", 560);
        _list.SelectedIndexChanged += (_, _) => ShowPreview();
        _list.KeyDown += OnDraftKeyDown;
        _list.DrawSubItem += DrawDraftRow;

        var listHost = Card(_list, DockStyle.Top, _settings.ListHeight);

        // Drag the divider to give the list more room and the preview less.
        var splitter = new Splitter
        {
            Dock = DockStyle.Top,
            Height = 6,
            BackColor = Theme.Window,
            MinExtra = 140,
            MinSize = 120
        };
        splitter.SplitterMoved += (_, _) =>
        {
            _settings.ListHeight = listHost.Height;
            _settings.Save();
        };
        splitter.Paint += (_, e) =>
        {
            using var grip = new SolidBrush(Theme.Line);
            var w = 42;
            e.Graphics.FillRectangle(grip, (splitter.Width - w) / 2, 2, w, 2);
        };

        _previewMeta.Dock = DockStyle.Top;
        _previewMeta.Height = 24;
        _previewMeta.ForeColor = Theme.Faint;
        _previewMeta.Font = Theme.Small;
        _previewMeta.Padding = new Padding(2, 6, 0, 0);

        _preview.Multiline = true;
        _preview.ReadOnly = true;
        _preview.ScrollBars = ScrollBars.Vertical;
        _preview.BorderStyle = BorderStyle.None;
        _preview.BackColor = Theme.Surface;
        _preview.ForeColor = Theme.Text;
        _preview.Font = Theme.Monospace();
        _preview.Dock = DockStyle.Fill;

        var previewHost = Card(_preview, DockStyle.Fill, 0);

        var copy = new Button { Text = "Copy to clipboard", Width = 156 };
        Theme.StyleButton(copy, primary: true);
        copy.Click += (_, _) => CopySelected();

        var forget = new Button { Text = "Forget", Width = 92 };
        Theme.StyleButton(forget);
        forget.Click += (_, _) => ForgetSelected();

        var note = new Label
        {
            Text = "Encrypted for this Windows account. Nothing leaves this machine.",
            AutoSize = true,
            ForeColor = Theme.Faint,
            Font = Theme.Small,
            Padding = new Padding(14, 9, 0, 0)
        };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            BackColor = Theme.Window,
            Padding = new Padding(0, 10, 0, 0)
        };
        actions.Controls.AddRange(new Control[] { copy, forget, note });

        var lower = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window };
        lower.Controls.Add(previewHost);
        lower.Controls.Add(_previewMeta);
        lower.Controls.Add(actions);

        // Docking stacks the last control added nearest the edge. The splitter goes
        // between the list and everything below it.
        page.Controls.Add(lower);
        page.Controls.Add(splitter);
        page.Controls.Add(listHost);
        return page;
    }

    private Panel BuildImagesPage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window, Padding = new Padding(0, 10, 0, 0) };

        StyleList(_imageList);
        _imageList.Columns.Add("Session", 190);
        _imageList.Columns.Add("Saved", 110);
        _imageList.Columns.Add("Size", 120);
        _imageList.Columns.Add("Weight", 100);
        _imageList.SelectedIndexChanged += (_, _) => ShowImage();
        _imageList.KeyDown += OnImageKeyDown;
        _imageList.DrawSubItem += DrawImageRow;

        var listHost = Card(_imageList, DockStyle.Top, 180);

        _imagePreview.Dock = DockStyle.Fill;
        _imagePreview.SizeMode = PictureBoxSizeMode.Zoom;
        _imagePreview.BackColor = Theme.Surface;
        var previewHost = Card(_imagePreview, DockStyle.Fill, 0);

        var save = new Button { Text = "Save as PNG", Width = 126 };
        Theme.StyleButton(save, primary: true);
        save.Click += (_, _) => ExportSelected();

        var forget = new Button { Text = "Forget", Width = 92 };
        Theme.StyleButton(forget);
        forget.Click += (_, _) => ForgetImage();

        _imageHint.AutoSize = true;
        _imageHint.ForeColor = Theme.Faint;
        _imageHint.Font = Theme.Small;
        _imageHint.Padding = new Padding(14, 9, 0, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 46,
            BackColor = Theme.Window,
            Padding = new Padding(0, 10, 0, 0)
        };
        actions.Controls.AddRange(new Control[] { save, forget, _imageHint });

        var lower = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Window };
        lower.Controls.Add(previewHost);
        lower.Controls.Add(actions);

        page.Controls.Add(lower);
        page.Controls.Add(listHost);
        return page;
    }

    private static Panel Card(Control inner, DockStyle dock, int height)
    {
        var card = new Panel
        {
            Dock = dock,
            BackColor = Theme.Surface,
            Padding = new Padding(1)
        };
        if (height > 0) card.Height = height;
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Line);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };
        inner.Dock = DockStyle.Fill;
        card.Controls.Add(inner);
        return card;
    }

    /// <summary>
    /// Keeps the last column filling the width. The framework paints any space past the
    /// last column in its own light grey. In dark mode that shows as a bright block.
    /// </summary>
    private static void FillLastColumn(ListView list)
    {
        if (list.Columns.Count == 0 || list.ClientSize.Width <= 0) return;
        var used = 0;
        for (var i = 0; i < list.Columns.Count - 1; i++) used += list.Columns[i].Width;
        var last = list.ClientSize.Width - used;
        if (last > 60) list.Columns[^1].Width = last;
    }

    private static void StyleList(ListView list)
    {
        list.View = View.Details;
        list.FullRowSelect = true;
        list.MultiSelect = false;
        list.HideSelection = false;
        list.BorderStyle = BorderStyle.None;
        list.BackColor = Theme.Surface;
        list.ForeColor = Theme.Text;
        list.Font = Theme.Ui;
        list.OwnerDraw = true;
        list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        list.Resize += (_, _) => FillLastColumn(list);
        list.HandleCreated += (_, _) => FillLastColumn(list);
        list.DrawColumnHeader += (s, e) =>
        {
            using var back = new SolidBrush(Theme.Raised);
            e.Graphics.FillRectangle(back, e.Bounds);
            using var line = new Pen(Theme.Line);
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            using var text = new SolidBrush(Theme.Muted);
            var box = e.Bounds with { X = e.Bounds.X + 10, Width = e.Bounds.Width - 12 };
            e.Graphics.DrawString(e.Header?.Text ?? string.Empty, Theme.Small, text, box,
                new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap });
        };
    }

    private static void DrawRowBackground(DrawListViewSubItemEventArgs e)
    {
        var selected = e.Item is not null && e.Item.Selected;
        using var back = new SolidBrush(selected ? Theme.Selection : Theme.Surface);
        e.Graphics.FillRectangle(back, e.Bounds);
        if (!selected) return;
        using var edge = new SolidBrush(Theme.Accent);
        if (e.ColumnIndex == 0) e.Graphics.FillRectangle(edge, e.Bounds.Left, e.Bounds.Top, 3, e.Bounds.Height);
    }

    private static void DrawCell(DrawListViewSubItemEventArgs e, string text, Color colour, Font font)
    {
        using var brush = new SolidBrush(colour);
        var box = e.Bounds with { X = e.Bounds.X + 10, Width = Math.Max(4, e.Bounds.Width - 14) };
        e.Graphics.DrawString(text, font, brush, box,
            new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter });
    }

    private void DrawDraftRow(object? sender, DrawListViewSubItemEventArgs e)
    {
        DrawRowBackground(e);
        if (e.Item?.Tag is not Draft d) return;

        switch (e.ColumnIndex)
        {
            case 0:
                DrawCell(e, d.Session, Theme.Text, Theme.UiBold);
                break;
            case 1:
                DrawCell(e, Ago(d.SavedAt), Theme.Muted, Theme.Ui);
                break;
            case 2:
                var label = d.Live ? "typing" : "saved";
                var colour = d.Live ? Theme.Live : Theme.Faint;
                var pill = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 5, 54, e.Bounds.Height - 10);
                using (var fill = new SolidBrush(Color.FromArgb(38, colour)))
                    e.Graphics.FillRectangle(fill, pill);
                using (var brush = new SolidBrush(colour))
                    e.Graphics.DrawString(label, Theme.Small, brush, pill,
                        new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                break;
            default:
                DrawCell(e, OneLine(d.Text), Theme.Muted, Theme.Ui);
                break;
        }
    }

    private void DrawImageRow(object? sender, DrawListViewSubItemEventArgs e)
    {
        DrawRowBackground(e);
        if (e.Item?.Tag is not ImageNote n) return;
        var text = e.ColumnIndex switch
        {
            0 => n.Session,
            1 => Ago(n.SavedAt),
            2 => $"{n.Width} x {n.Height}",
            _ => $"{n.Bytes / 1024} KB"
        };
        DrawCell(e, text, e.ColumnIndex == 0 ? Theme.Text : Theme.Muted,
                 e.ColumnIndex == 0 ? Theme.UiBold : Theme.Ui);
    }

    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when;
        if (span.TotalSeconds < 45) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
        return when.ToString("dd MMM HH:mm");
    }

    private static string OneLine(string text)
    {
        var line = text.Replace("\r", " ").Replace("\n", " · ").Trim();
        while (line.Contains("  ")) line = line.Replace("  ", " ");
        return line.Length > 200 ? line[..200] + "..." : line;
    }

    /// <summary>
    /// A web box reports bare newlines, and a Windows text box only breaks on a carriage
    /// return and newline pair. Drafts are stored as typed and converted here for the
    /// screen and the clipboard.
    /// </summary>
    private static string ForWindows(string text)
        => text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");

    private Draft? Selected()
        => _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0].Tag as Draft;

    private ImageNote? SelectedImage()
        => _imageList.SelectedItems.Count == 0 ? null : _imageList.SelectedItems[0].Tag as ImageNote;

    private void OnDraftKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete) ForgetSelected();
        if (e.Control && e.KeyCode == Keys.C) CopySelected();
    }

    private void OnImageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete) ForgetImage();
    }

    private void ShowPreview()
    {
        var d = Selected();
        _preview.Text = d is null ? string.Empty : ForWindows(d.Text);
        // Setting Text selects all of it. Without this the whole draft shows highlighted
        // after every refresh.
        _preview.SelectionStart = 0;
        _preview.SelectionLength = 0;
        _previewMeta.Text = d is null
            ? string.Empty
            : $"{d.Session}   ·   {d.Text.Length:N0} characters   ·   {(d.Live ? "still being typed" : "saved")}   ·   {d.SavedAt:dd MMM HH:mm:ss}";
    }

    private void ShowImage()
    {
        var note = SelectedImage();
        _imagePreview.Image?.Dispose();
        _imagePreview.Image = note is null ? null : _images.Open(note.Id);
    }

    private void CopySelected()
    {
        var d = Selected();
        if (d is null) return;
        try { Clipboard.SetText(ForWindows(d.Text)); }
        catch { /* another process can hold the clipboard open */ }
    }

    private void ForgetSelected()
    {
        var d = Selected();
        if (d is null) return;
        _store.Forget(d.Id);
        Reload();
    }

    private void ForgetImage()
    {
        var n = SelectedImage();
        if (n is null) return;
        _imagePreview.Image?.Dispose();
        _imagePreview.Image = null;
        _images.Forget(n.Id);
        Reload();
    }

    private void ExportSelected()
    {
        var n = SelectedImage();
        if (n is null) return;
        using var dialog = new SaveFileDialog { Filter = "PNG image|*.png", FileName = n.Id + ".png" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _images.Export(n.Id, dialog.FileName);
    }

    private bool Matches(string session, string text)
    {
        var q = _search.Text.Trim();
        if (q.Length == 0) return true;
        return session.Contains(q, StringComparison.OrdinalIgnoreCase)
            || text.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A signature of what is on screen. Rebuilding the list loses the scroll position and
    /// the selection in the preview. It is rebuilt only when this signature changes.
    /// </summary>
    private static string Signature(IEnumerable<Draft> drafts, IEnumerable<ImageNote> images)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var d in drafts) sb.Append(d.Id).Append(d.Session).Append(d.Text.Length).Append(d.Live).Append('|');
        sb.Append('#');
        foreach (var n in images) sb.Append(n.Id).Append('|');
        return sb.ToString();
    }

    private string _shown = string.Empty;

    public void Reload()
    {
        var drafts = _store.All().Where(d => Matches(d.Session, d.Text)).ToList();
        var images = _images.All().Where(n => Matches(n.Session, string.Empty)).ToList();

        var signature = Signature(drafts, images);
        if (signature == _shown)
        {
            // Nothing changed. Touching the lists would scroll back to the top and drop
            // the selection mid-copy.
            UpdateCounts(drafts, images);
            return;
        }
        _shown = signature;

        var keepDraft = Selected()?.Id;
        var keepImage = SelectedImage()?.Id;
        var topIndex = _list.TopItem?.Index ?? 0;

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var d in drafts)
            _list.Items.Add(new ListViewItem(new[] { d.Session, "", "", "" }) { Tag = d });
        _list.EndUpdate();

        var draftRow = _list.Items.Cast<ListViewItem>().FirstOrDefault(i => (i.Tag as Draft)?.Id == keepDraft);
        if (draftRow is not null) draftRow.Selected = true;
        else if (_list.Items.Count > 0) _list.Items[0].Selected = true;

        // Put the scroll back where it was.
        if (topIndex > 0 && topIndex < _list.Items.Count) _list.TopItem = _list.Items[topIndex];
        ShowPreview();

        _imageList.BeginUpdate();
        _imageList.Items.Clear();
        foreach (var n in images)
            _imageList.Items.Add(new ListViewItem(new[] { n.Session, "", "", "" }) { Tag = n });
        _imageList.EndUpdate();

        var imageRow = _imageList.Items.Cast<ListViewItem>().FirstOrDefault(i => (i.Tag as ImageNote)?.Id == keepImage);
        if (imageRow is not null) imageRow.Selected = true;
        else if (_imageList.Items.Count > 0) _imageList.Items[0].Selected = true;
        if (imageRow is null || _imagePreview.Image is null) ShowImage();

        UpdateCounts(drafts, images);
    }

    private void UpdateCounts(List<Draft> drafts, List<ImageNote> images)
    {
        _imageHint.Text = _images.Enabled
            ? "Clipboard images are being saved. Only image formats are read."
            : "Off. Switch on \"Save clipboard images\" in the tray menu.";

        var sessions = drafts.Select(d => d.Session).Distinct().Count();
        _subheading.Text = drafts.Count == 0
            ? "Nothing saved yet"
            : $"{drafts.Count} draft{(drafts.Count == 1 ? "" : "s")} across {sessions} session{(sessions == 1 ? "" : "s")}"
              + (images.Count > 0 ? $"   ·   {images.Count} image{(images.Count == 1 ? "" : "s")}" : "");

        var draftsTab = $"Drafts ({drafts.Count})";
        var imagesTab = $"Images ({images.Count})";
        if (draftsTab == _draftsTabText && imagesTab == _imagesTabText) return;
        _draftsTabText = draftsTab;
        _imagesTabText = imagesTab;
        _tabStrip.Invalidate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Keep the window alive so reopening from the tray is instant.
        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// Minimise hides the window. The tray icon brings it back.
    /// </summary>
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState != FormWindowState.Minimized) return;
        Hide();
        WindowState = FormWindowState.Normal;
    }
}
