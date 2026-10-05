using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Drawing.Imaging;

namespace DraftKeeper;

public sealed record ImageNote(string Id, string Session, DateTimeOffset SavedAt, int Width, int Height, long Bytes);

/// <summary>
/// Optional capture of images placed on the clipboard, for drafts that include a
/// screenshot. Off until switched on. Only image formats are read; clipboard text never
/// is.
///
/// Files are encrypted the same way drafts are. Polling only happens while the feature
/// is on.
/// </summary>
public sealed class ImageStore
{
    private const int MaxImages = 20;

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    private readonly string _dir;
    private readonly string _index;
    private readonly object _gate = new();
    private readonly List<ImageNote> _notes = new();

    private uint _lastSequence;

    public bool Enabled { get; set; }
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
    public string CurrentSession { get; set; } = "unknown";

    public event Action? Changed;

    /// <summary>Retention is a constructor argument because loading purges. See DraftStore.</summary>
    public ImageStore(TimeSpan retention)
    {
        Retention = retention;
        _dir = DataFolder.File("images");
        Directory.CreateDirectory(_dir);
        _index = DataFolder.File("images.dat");
        _lastSequence = GetClipboardSequenceNumber();
        Load();
    }

    public IReadOnlyList<ImageNote> All()
    {
        lock (_gate)
        {
            Purge();
            return _notes.OrderByDescending(n => n.SavedAt).ToList();
        }
    }

    /// <summary>Call from the user interface thread. Clipboard access requires it.</summary>
    public void Poll()
    {
        if (!Enabled) return;

        var sequence = GetClipboardSequenceNumber();
        if (sequence == _lastSequence) return;
        _lastSequence = sequence;

        Image? image = null;
        try
        {
            // Only ask about images. Clipboard text is never read.
            if (!Clipboard.ContainsImage()) return;
            image = Clipboard.GetImage();
        }
        catch { return; }

        if (image is null) return;

        try
        {
            using var buffer = new MemoryStream();
            image.Save(buffer, ImageFormat.Png);
            var bytes = buffer.ToArray();

            var id = Guid.NewGuid().ToString("n")[..12];
            File.WriteAllBytes(Path.Combine(_dir, id + ".bin"), Dpapi.Protect(bytes));

            lock (_gate)
            {
                _notes.Add(new ImageNote(id, CurrentSession, DateTimeOffset.Now,
                                         image.Width, image.Height, bytes.LongLength));
                Purge();
                Save();
            }
            Changed?.Invoke();
        }
        catch { /* a clipboard image that will not encode is not worth a crash */ }
        finally { image.Dispose(); }
    }

    public Image? Open(string id)
    {
        try
        {
            var file = Path.Combine(_dir, id + ".bin");
            if (!File.Exists(file)) return null;
            var bytes = Dpapi.Unprotect(File.ReadAllBytes(file));
            using var buffer = new MemoryStream(bytes);
            return Image.FromStream(buffer);
        }
        catch { return null; }
    }

    public void Export(string id, string destination)
    {
        using var image = Open(id);
        image?.Save(destination, ImageFormat.Png);
    }

    public void Forget(string id)
    {
        lock (_gate)
        {
            _notes.RemoveAll(n => n.Id == id);
            Delete(id);
            Save();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var n in _notes) Delete(n.Id);
            _notes.Clear();
            Save();
        }
    }

    private void Delete(string id)
    {
        try { File.Delete(Path.Combine(_dir, id + ".bin")); } catch { /* already gone */ }
    }

    private void Purge()
    {
        var cutoff = DateTimeOffset.Now - Retention;
        foreach (var n in _notes.Where(n => n.SavedAt < cutoff).ToList())
        {
            Delete(n.Id);
            _notes.Remove(n);
        }
        while (_notes.Count > MaxImages)
        {
            var oldest = _notes.OrderBy(n => n.SavedAt).First();
            Delete(oldest.Id);
            _notes.Remove(oldest);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_index)) return;
            var json = Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(_index)));
            var loaded = JsonSerializer.Deserialize<List<ImageNote>>(json);
            if (loaded is not null) _notes.AddRange(loaded);
            Purge();
        }
        catch { _notes.Clear(); }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_notes);
            File.WriteAllBytes(_index, Dpapi.Protect(Encoding.UTF8.GetBytes(json)));
        }
        catch { /* losing the index must not take the app down */ }
    }
}
