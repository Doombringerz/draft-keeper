using System.IO;
using System.Text;
using System.Text.Json;

namespace DraftKeeper;

public sealed record Draft(
    string Id,
    string Owner,
    string Session,
    string Text,
    DateTimeOffset SavedAt,
    bool Live);

/// <summary>
/// Encrypted draft storage in the data folder. Nothing here is sent anywhere; the program
/// has no network code.
///
/// A live row is looked up by the box that owns it (<c>owner</c>), never by chat name.
/// The name can change while the row is being typed.
/// </summary>
public sealed class DraftStore
{
    private const int HistoryPerSession = 3;
    private const int HardCap = 300;
    private const int MinChars = 12;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<Draft> _drafts = new();

    /// <summary>Text the user deleted, per box. It is not re-added while it stays in the box.</summary>
    private readonly Dictionary<string, string> _dismissed = new();

    /// <summary>
    /// Set DRAFTKEEPER_LOG to a file path to record every removal and the reason for it.
    /// Several paths remove rows, and from the outside a missing row looks the same
    /// whichever one did it.
    /// </summary>
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("DRAFTKEEPER_LOG");
    private static readonly object LogGate = new();

    private static void LogRemoval(string reason, Draft row)
    {
        if (LogPath is null) return;
        try
        {
            // The log is plain text next to an encrypted store. It holds the row and the
            // reason, never the text.
            lock (LogGate)
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:HH:mm:ss.fff}  REMOVE {reason,-14} id={row.Id} " +
                    $"session='{row.Session}' age={(DateTimeOffset.Now - row.SavedAt).TotalHours:F1}h " +
                    $"chars={row.Text.Length} live={row.Live}{Environment.NewLine}");
        }
        catch { /* logging must never affect behaviour */ }
    }

    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Retention comes in through the constructor. Loading purges, and an object
    /// initialiser would set it only after the purge had used the default and deleted
    /// drafts the user meant to keep.
    /// </summary>
    public DraftStore(TimeSpan retention)
    {
        Retention = retention;
        _path = DataFolder.File("drafts.dat");
        Load();
    }

    public string FilePath => _path;

    public IReadOnlyList<Draft> All()
    {
        lock (_gate)
        {
            Purge();
            return _drafts.OrderByDescending(d => d.SavedAt).ToList();
        }
    }

    /// <summary>
    /// Overwrites the row this box owns, or starts one. Never adds a second row for the
    /// same box, whatever its label.
    /// </summary>
    public void Update(string owner, string session, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < MinChars) return;

        lock (_gate)
        {
            // A row the user deleted stays gone while the same text sits in the box. It
            // comes back once the text changes.
            if (_dismissed.TryGetValue(owner, out var gone) && gone == text) return;

            var index = _drafts.FindIndex(d => d.Live && d.Owner == owner);
            if (index >= 0)
            {
                var row = _drafts[index];
                // The watcher decides the label. It knows whether the label has settled.
                var label = session != "unknown" ? session : row.Session;
                if (row.Text == text && row.Session == label) return;
                _drafts[index] = row with { Text = text, Session = label, SavedAt = DateTimeOffset.Now };
            }
            else
            {
                // Opening a chat puts its unsent text back in the box. When that exact text
                // is already saved for the chat, that row carries on, with its own time.
                var saved = _drafts.FindIndex(d => !d.Live && d.Session == session && d.Text == text);
                if (saved >= 0)
                    _drafts[saved] = _drafts[saved] with { Owner = owner, Live = true };
                else
                    _drafts.Add(new Draft(Guid.NewGuid().ToString("n")[..10], owner, session, text,
                                          DateTimeOffset.Now, Live: true));
            }
            _dismissed.Remove(owner);
            Purge();
            Save();
        }
    }

    /// <summary>
    /// Closes the row a box owned after its buffer was replaced or sent. The row keeps
    /// the label it was given while it was being typed.
    /// </summary>
    public void Seal(string owner)
    {
        lock (_gate)
        {
            var index = _drafts.FindIndex(d => d.Live && d.Owner == owner);
            if (index < 0) return;
            var row = _drafts[index] with { Live = false };
            _drafts[index] = row;
            FoldCopies(row.Session);
            TrimSession(row.Session);
            Purge();
            Save();
        }
    }

    /// <summary>
    /// Corrects the label on the row a box is still typing into, once the window has
    /// settled enough to name the chat. Finished rows are never relabelled.
    /// </summary>
    public void Relabel(string owner, string session)
    {
        if (session == "unknown") return;
        lock (_gate)
        {
            var index = _drafts.FindIndex(d => d.Live && d.Owner == owner);
            if (index < 0 || _drafts[index].Session == session) return;
            _drafts[index] = _drafts[index] with { Session = session };
            Save();
        }
    }

    /// <summary>
    /// Drops the finished rows of a chat whose text was confirmed sent. Live rows are
    /// left alone while something is still being typed there. When the box the text came
    /// from is known, that box does not save the same text again while it still shows it.
    /// </summary>
    public bool ForgetSent(string session, string text, string? owner)
    {
        lock (_gate)
        {
            var rows = _drafts.Where(d => !d.Live && d.Session == session && d.Text == text).ToList();
            if (rows.Count == 0) return false;
            foreach (var row in rows)
            {
                LogRemoval("confirmed-sent", row);
                _drafts.Remove(row);
            }
            if (owner is not null) _dismissed[owner] = text;
            Save();
            return true;
        }
    }

    public IReadOnlyList<string> FinishedTexts(string session)
    {
        lock (_gate)
            return _drafts.Where(d => !d.Live && d.Session == session).Select(d => d.Text).Distinct().ToList();
    }

    public void Forget(string id)
    {
        lock (_gate)
        {
            var row = _drafts.FirstOrDefault(d => d.Id == id);
            if (row is not null && row.Live) _dismissed[row.Owner] = row.Text;
            if (row is not null) LogRemoval("user-forget", row);
            _drafts.RemoveAll(d => d.Id == id);
            Save();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            foreach (var row in _drafts.Where(d => d.Live)) _dismissed[row.Owner] = row.Text;
            foreach (var row in _drafts) LogRemoval("user-clear-all", row);
            _drafts.Clear();
            Save();
        }
    }

    /// <summary>
    /// Drops a finished row whose whole text is inside another finished row of the same
    /// chat. No text is lost. The longer row stays, and of two exact copies the older one
    /// stays. Without this, copies fill the chat's three places and push real drafts out.
    /// </summary>
    private void FoldCopies(string session)
    {
        var rows = _drafts
            .Where(d => !d.Live && d.Session == session)
            .OrderByDescending(d => d.Text.Length)
            .ThenBy(d => d.SavedAt)
            .ToList();

        for (var i = 0; i < rows.Count; i++)
        {
            for (var j = rows.Count - 1; j > i; j--)
            {
                if (!rows[i].Text.Contains(rows[j].Text, StringComparison.Ordinal)) continue;
                LogRemoval("copy", rows[j]);
                _drafts.Remove(rows[j]);
                rows.RemoveAt(j);
            }
        }
    }

    private void TrimSession(string session)
    {
        var stale = _drafts
            .Where(d => !d.Live && d.Session == session)
            .OrderByDescending(d => d.SavedAt)
            .Skip(HistoryPerSession)
            .ToList();
        foreach (var d in stale) { LogRemoval("trim-session", d); _drafts.Remove(d); }
    }

    private void Purge()
    {
        var cutoff = DateTimeOffset.Now - Retention;
        foreach (var d in _drafts.Where(d => !d.Live && d.SavedAt < cutoff).ToList())
        {
            LogRemoval("retention", d);
            _drafts.Remove(d);
        }
        if (_drafts.Count > HardCap)
        {
            var excess = _drafts.Where(d => !d.Live)
                                .OrderBy(d => d.SavedAt)
                                .Take(_drafts.Count - HardCap)
                                .ToList();
            foreach (var d in excess) { LogRemoval("hard-cap", d); _drafts.Remove(d); }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = Encoding.UTF8.GetString(Dpapi.Unprotect(File.ReadAllBytes(_path)));
            var loaded = JsonSerializer.Deserialize<List<Draft>>(json);
            if (loaded is null) return;
            // A row left live by a crash is finished. The process that owned it is gone.
            foreach (var d in loaded) _drafts.Add(d with { Live = false });
            foreach (var session in _drafts.Select(d => d.Session).Distinct().ToList())
            {
                FoldCopies(session);
                TrimSession(session);
            }
            Purge();
        }
        catch
        {
            // A file written by another account cannot be decrypted, and a write cut
            // short by a power loss cannot be parsed. Either way, start empty.
            _drafts.Clear();
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_drafts);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, Dpapi.Protect(Encoding.UTF8.GetBytes(json)));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Losing a save must never take the app down with it.
        }
    }
}
