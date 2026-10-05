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

// The drafts, encrypted, in the data folder. Nothing gets sent anywhere.
// A live row belongs to a box (owner), not a chat name. The name can still change while you type.
public sealed class DraftStore
{
    private const int HistoryPerSession = 3;
    private const int HardCap = 300;
    private const int MinChars = 12;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<Draft> _drafts = new();

    // Drafts you deleted, per box. They don't come back while the same text sits in the box.
    private readonly Dictionary<string, string> _dismissed = new();

    // DRAFTKEEPER_LOG=<file> also logs every removal and why. From the outside, a missing
    // draft looks the same no matter what removed it.
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("DRAFTKEEPER_LOG");
    private static readonly object LogGate = new();

    private static void LogRemoval(string reason, Draft row)
    {
        if (LogPath is null) return;
        try
        {
            // Plain text next to an encrypted store. So: which row and why, never what it said.
            lock (LogGate)
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:HH:mm:ss.fff}  REMOVE {reason,-14} id={row.Id} " +
                    $"session='{row.Session}' age={(DateTimeOffset.Now - row.SavedAt).TotalHours:F1}h " +
                    $"chars={row.Text.Length} live={row.Live}{Environment.NewLine}");
        }
        catch { /* a broken log never breaks saving */ }
    }

    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    // Retention has to come in here. Loading purges old drafts, and setting it afterwards
    // meant the purge ran on the 24 hour default and binned drafts you wanted to keep.
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

    // One live row per box. Typing overwrites it.
    public void Update(string owner, string session, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Length < MinChars) return;

        lock (_gate)
        {
            if (_dismissed.TryGetValue(owner, out var gone) && gone == text) return;

            var index = _drafts.FindIndex(d => d.Live && d.Owner == owner);
            if (index >= 0)
            {
                var row = _drafts[index];
                var label = session != "unknown" ? session : row.Session;
                if (row.Text == text && row.Session == label) return;
                _drafts[index] = row with { Text = text, Session = label, SavedAt = DateTimeOffset.Now };
            }
            else
            {
                // Reopening a chat puts its old draft back in the box. Already saved? Then
                // pick that row up again, old time and all, instead of saving a copy.
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

    // The box moved on. Its row is done and keeps the chat it was typed in.
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

    // Live rows only. A finished draft never moves to another chat.
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

    // Sent means it goes. Live rows stay, you're still typing there.
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

    // A draft that's fully inside another draft of the same chat is a copy. Keep the
    // longer one, or the older one if they're equal. Copies used to fill up a chat's
    // three spots and push the real drafts out.
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
            // Anything still marked live is left over from a crash. Done, either way.
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
            // Another account's file, or a write cut off by a power loss. Start empty.
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
            // One failed save isn't worth crashing over. Next keystroke tries again.
        }
    }
}
