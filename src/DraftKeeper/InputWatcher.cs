using System.Diagnostics;
using System.Windows.Automation;

namespace DraftKeeper;

/// <summary>
/// Watches the chat boxes of each editor window.
///
/// Every few seconds each window is searched for boxes by name. A box's value is read
/// when Windows reports a change. Besides the value, it reads the name of the chat the
/// box belongs to and, once a box empties, whether its last text now shows in that chat.
/// No keyboard hook is installed.
///
/// Windows reports a password field's value as bullets. A password is never captured.
/// </summary>
public sealed class InputWatcher : IDisposable
{
    private static readonly string[] BoxNames = { "Message input", "Ask a side question" };

    /// <summary>
    /// An empty box reports its placeholder as its value. These are never saved.
    /// </summary>
    private static readonly string[] Placeholders =
    {
        "queue another message", "type your message", "ask a side question",
        "message", "type a message", "send a message", "ask claude anything",
        "reply to claude", "what would you like to do"
    };

    /// <summary>
    /// Keyboard hints an empty box shows in place of a placeholder. They are not drafts.
    /// Their wording varies. They are matched by fragment.
    /// </summary>
    private static readonly string[] HintFragments =
    {
        "to focus or unfocus", "press enter to send", "shift+enter", "shift + enter",
        "for newline", "for a new line", "to send a message", "esc to"
    };

    private const int QuietMs = 900;

    // Must stay above ProcessNames: static fields initialise in the order written.
    private static readonly string[] BuiltInEditors = { "Code", "Code - Insiders", "Cursor", "VSCodium", "Windsurf" };

    /// <summary>
    /// Editors to look in. Set DRAFTKEEPER_EXTRA_PROCESSES to a comma separated list
    /// to cover an editor that is not in the default set.
    /// </summary>
    private static readonly string[] ProcessNames = BuildProcessNames();

    private static string[] BuildProcessNames()
    {
        var names = new List<string>(BuiltInEditors);
        var extra = Environment.GetEnvironmentVariable("DRAFTKEEPER_EXTRA_PROCESSES");
        if (!string.IsNullOrWhiteSpace(extra))
            names.AddRange(extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Set DRAFTKEEPER_LOG to a file path to record what the watcher decided. Lengths and
    /// chat names only, never draft text.
    /// </summary>
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("DRAFTKEEPER_LOG");
    private static readonly object LogGate = new();

    private static void Log(string message)
    {
        if (LogPath is null) return;
        try
        {
            lock (LogGate)
                System.IO.File.AppendAllText(LogPath,
                    $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch { /* logging must never affect behaviour */ }
    }

    private readonly DraftStore _store;
    private readonly System.Threading.Timer _discovery;
    private readonly System.Threading.Timer _flush;
    private readonly System.Threading.Timer _checks;
    private readonly object _gate = new();
    private readonly Dictionary<string, Tracked> _tracked = new();

    private bool _disposed;

    /// <summary>Raised with the chat label whenever a draft is being written.</summary>
    public event Action<string>? Activity;

    public bool Paused { get; set; }

    public int WatchedCount
    {
        get { lock (_gate) return _tracked.Count; }
    }

    private sealed class Tracked
    {
        public required IntPtr Window { get; init; }
        public required string Owner { get; init; }
        public required AutomationElement Element { get; init; }
        public required AutomationPropertyChangedEventHandler Handler { get; init; }
        public string LastText = string.Empty;
        public string? Pending;
        public long PendingAt;

        /// <summary>The chat this buffer belongs to, once it is known.</summary>
        public string Session = "unknown";

        /// <summary>The named document around the box, and its name. Null when there is none.</summary>
        public AutomationElement? Document;
        public string? OwnLabel;

        /// <summary>
        /// For a box without a named document: false until the tab strip or title has
        /// held still long enough to name the chat. Either can change before the text.
        /// </summary>
        public bool LabelSettled;
        public string LastTitleSeen = string.Empty;
        public long TitleSteadySince;
    }

    private int _sweeping;
    private int _flushing;
    private int _checking;

    /// <summary>
    /// Three timers. Saving touches nothing outside this process and never waits on a slow
    /// editor. The searches and reads that go through the editor run on the other two.
    /// </summary>
    public InputWatcher(DraftStore store)
    {
        _store = store;
        _discovery = new System.Threading.Timer(_ => RunAlone(ref _sweeping, "sweep", Sweep), null,
                                                TimeSpan.Zero, TimeSpan.FromSeconds(4));
        _flush = new System.Threading.Timer(_ => RunAlone(ref _flushing, "flush", Flush), null,
                                            TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(400));
        _checks = new System.Threading.Timer(_ => RunAlone(ref _checking, "checks", Check), null,
                                             TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(400));
    }

    /// <summary>
    /// A timer does not wait for its previous callback. Without this, a slow pass over a
    /// large window is overlapped by the next, which repeats the same searches.
    /// </summary>
    private static void RunAlone(ref int busy, string name, Action work)
    {
        if (Interlocked.Exchange(ref busy, 1) == 1)
        {
            Log($"{name} still running, skipped");
            return;
        }
        try { work(); }
        finally { Volatile.Write(ref busy, 0); }
    }

    private static bool IsPlaceholder(string text)
    {
        var t = text.Trim().TrimEnd('.', '…').ToLowerInvariant();
        if (Placeholders.Any(p => t == p)) return true;
        // Hints are short and describe a key, never something a person is drafting.
        return t.Length <= 80 && HintFragments.Any(h => t.Contains(h, StringComparison.Ordinal));
    }

    /// <summary>
    /// Whether two successive buffer values are the same draft still being worked on:
    /// typed onto, cut back, or edited in place. A prefix check alone treats an edit in
    /// the middle as a new draft. A different buffer shares almost nothing with the old.
    /// </summary>
    internal static bool SameDraft(string before, string after)
    {
        if (before.Length == 0 || after.Length == 0) return false;

        // Typed onto or backspaced: one draft, however short the old text was.
        if (after.StartsWith(before, StringComparison.Ordinal) ||
            before.StartsWith(after, StringComparison.Ordinal) ||
            after.EndsWith(before, StringComparison.Ordinal) ||
            before.EndsWith(after, StringComparison.Ordinal)) return true;

        var min = Math.Min(before.Length, after.Length);
        var max = Math.Max(before.Length, after.Length);

        var prefix = 0;
        while (prefix < min && before[prefix] == after[prefix]) prefix++;

        var suffix = 0;
        while (suffix < min - prefix &&
               before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix]) suffix++;

        // Edited in place: at least half of the longer text survived.
        return (prefix + suffix) * 2 >= max;
    }

    private static string SessionFromTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "unknown";
        // "Ledger - my-workspace (Workspace) - Visual Studio Code" -> "Ledger"
        var cut = title.IndexOf(" - ", StringComparison.Ordinal);
        var head = (cut > 0 ? title[..cut] : title).TrimStart('●', '*', ' ').Trim();

        // A title made of the first line of a long prompt is not a tab name.
        if (head.Length > 40 || head.EndsWith('…')) return "unknown";
        return head.Length == 0 ? "unknown" : head;
    }

    private void Sweep()
    {
        if (_disposed || Paused) return;

        try
        {
            var windows = EditorWindows();
            var live = new HashSet<IntPtr>(windows);

            foreach (var handle in windows) Poll(handle);

            // Every window, every sweep. A box that was replaced leaves its old element
            // behind, and that element can keep returning its last text instead of failing.
            foreach (var handle in windows) Resolve(handle);

            lock (_gate)
            {
                foreach (var key in _tracked.Where(kv => !live.Contains(kv.Value.Window))
                                            .Select(kv => kv.Key).ToList())
                {
                    Log($"retire (window gone) {key}");
                    Retire(key);
                }
            }
            Log($"sweep done: {windows.Count} window(s), {WatchedCount} box(es) tracked");
        }
        catch (Exception ex)
        {
            // The tree changes under a sweep all the time. The next one runs in four seconds.
            Log($"SWEEP THREW {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Finds every chat box in the window and keeps each tracked element in step with the one on screen.</summary>
    private void Resolve(IntPtr handle)
    {
        AutomationElement window;
        try
        {
            var found = AutomationElement.FromHandle(handle);
            if (found is null) return;
            window = found;
        }
        catch { return; }

        foreach (var boxName in BoxNames)
        {
            // A search during a rebuild can hit the node that just went away. That is
            // when the new box matters most. Try again.
            AutomationElementCollection? matches = null;
            var cond = new PropertyCondition(AutomationElement.NameProperty, boxName);
            for (var attempt = 0; attempt < 3 && matches is null; attempt++)
            {
                try { matches = window.FindAll(TreeScope.Descendants, cond); }
                catch (ElementNotAvailableException)
                {
                    Log($"FindAll '{boxName}' hit a dead node, retry {attempt + 1}");
                    Thread.Sleep(120);
                }
                catch (Exception ex) { Log($"FindAll '{boxName}' threw {ex.GetType().Name}"); break; }
            }
            if (matches is null) continue;

            if (matches.Count > 0) Log($"resolve: '{boxName}' -> {matches.Count} element(s)");

            var seen = new HashSet<string>();
            for (var i = 0; i < matches.Count; i++)
            {
                var owner = matches.Count > 1 ? $"{handle}|{boxName}#{i}" : $"{handle}|{boxName}";
                var box = matches[i];
                seen.Add(owner);

                Tracked? existing;
                lock (_gate) _tracked.TryGetValue(owner, out existing);

                if (existing is not null)
                {
                    var stillCurrent = false;
                    var why = "runtime id differs";
                    try { stillCurrent = Automation.Compare(existing.Element, box); }
                    catch (Exception ex) { why = $"compare threw {ex.GetType().Name}"; }
                    if (stillCurrent) continue;
                    Log($"element replaced for {owner} ({why}), moving subscription");

                    // Another box is in this place now: the same chat rebuilt, or another
                    // chat's tab. Move the subscription across with what is known about
                    // the buffer. Observe tells the two cases apart by the box's document.
                    Swap(existing, box, owner, handle);

                    Tracked? moved;
                    lock (_gate) _tracked.TryGetValue(owner, out moved);
                    if (moved is null) continue;

                    string current;
                    try { current = ReadText(box); } catch { continue; }
                    lock (_gate) Observe(moved, current, CurrentSession(handle));
                    continue;
                }

                var (document, label) = NamedDocument(box);
                Tracked? self = null;
                var tracked = new Tracked
                {
                    Window = handle,
                    Owner = owner,
                    Element = box,
                    Handler = (_, e) => OnChanged(self!, e),
                    Document = document,
                    OwnLabel = label,
                    Session = label ?? CurrentSession(handle),
                    LabelSettled = label is not null
                };
                self = tracked;

                try
                {
                    Automation.AddAutomationPropertyChangedEventHandler(
                        box, TreeScope.Element, tracked.Handler, ValuePattern.ValueProperty);
                }
                catch { continue; }

                lock (_gate) _tracked[owner] = tracked;

                // What the box already holds is a draft too: a chat reopened after a crash,
                // or text typed while this program was not running.
                try
                {
                    var current = ReadText(box);
                    lock (_gate) Observe(tracked, current, tracked.Session);
                    CheckSavedDrafts(tracked, current);
                }
                catch { /* a reload can race the first read */ }
            }

            // A box not found this time is gone. Its tracking goes too, or one box ends up
            // tracked under two names once a second chat is on screen.
            var prefix = $"{handle}|{boxName}";
            lock (_gate)
            {
                foreach (var key in _tracked.Keys
                             .Where(k => (k == prefix || k.StartsWith(prefix + "#", StringComparison.Ordinal))
                                         && !seen.Contains(k))
                             .ToList())
                {
                    Log($"retire (box gone) {key}");
                    Retire(key);
                }
            }
        }
    }

    /// <summary>
    /// The named document the box sits in, and its name. VS Code gives every chat tab its
    /// own web view, and that view's document carries the tab's title. Read from the box
    /// itself, the label cannot belong to a different chat than the text. A box with no
    /// named document around it gets nulls and falls back to the tab strip and the title.
    /// </summary>
    private static (AutomationElement? Document, string? Label) NamedDocument(AutomationElement box)
    {
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            var node = walker.GetParent(box);
            for (var depth = 0; node is not null && depth < 8; depth++)
            {
                var type = node.Current.ControlType;
                if (type == ControlType.Window || type == ControlType.Pane) break;
                var name = node.Current.Name;
                if (type == ControlType.Document && !string.IsNullOrWhiteSpace(name))
                    return (node, name.Trim());
                node = walker.GetParent(node);
            }
        }
        catch { /* the view can close while this walks up */ }
        return (null, null);
    }

    private void Retire(string owner)
    {
        if (!_tracked.TryGetValue(owner, out var t)) return;
        if (t.Pending is not null) _store.Update(t.Owner, t.Session, t.Pending);
        _store.Seal(t.Owner);
        try { Automation.RemoveAutomationPropertyChangedEventHandler(t.Element, t.Handler); }
        catch { /* window already gone */ }
        _tracked.Remove(owner);
    }

    private static string ReadText(AutomationElement element)
        => element.GetCurrentPropertyValue(ValuePattern.ValueProperty) as string ?? string.Empty;

    /// <summary>
    /// Uses the value the event carries. Reading the box again gives what it holds by the
    /// time the event arrives, which can skip a box emptied for a moment between two
    /// messages and let the second overwrite the first.
    /// </summary>
    private void OnChanged(Tracked source, AutomationPropertyChangedEventArgs e)
    {
        if (Paused) return;

        string text;
        if (e.NewValue is string reported) text = reported;
        else
        {
            try { text = ReadText(source.Element); }
            catch { return; }
        }

        lock (_gate)
        {
            // An event from a box that has since been replaced can still arrive. Its text
            // belongs to that box, never to the chat that took its place.
            if (!_tracked.TryGetValue(source.Owner, out var t) || !ReferenceEquals(t, source)) return;
            Observe(t, text, CurrentSession(t.Window));
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int count);

    private delegate bool EnumProc(IntPtr window, IntPtr param);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr param);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder text, int count);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr window, out int processId);

    /// <summary>
    /// Every editor window. An Electron editor runs all its windows from one process, and
    /// the process's MainWindowHandle names only one of them.
    /// </summary>
    private static List<IntPtr> EditorWindows()
    {
        // The built-in editors all use Electron's window class, which rules out their
        // tooltips and helper windows. An editor from DRAFTKEEPER_EXTRA_PROCESSES is taken
        // as it comes.
        var electron = new HashSet<int>();
        var anyClass = new HashSet<int>();
        foreach (var name in ProcessNames)
        {
            var target = BuiltInEditors.Contains(name, StringComparer.OrdinalIgnoreCase) ? electron : anyClass;
            try
            {
                foreach (var proc in Process.GetProcessesByName(name)) target.Add(proc.Id);
            }
            catch { /* a process can exit between listing and reading */ }
        }

        var found = new List<IntPtr>();
        try
        {
            EnumWindows((handle, _) =>
            {
                try
                {
                    if (!IsWindowVisible(handle)) return true;

                    GetWindowThreadProcessId(handle, out var pid);
                    if (electron.Contains(pid))
                    {
                        var cls = new System.Text.StringBuilder(256);
                        GetClassName(handle, cls, cls.Capacity);
                        if (cls.ToString() != "Chrome_WidgetWin_1") return true;
                    }
                    else if (!anyClass.Contains(pid)) return true;

                    var title = new System.Text.StringBuilder(512);
                    if (GetWindowText(handle, title, title.Capacity) <= 0) return true;

                    found.Add(handle);
                }
                catch { /* skip a window that vanished mid-enumeration */ }
                return true;
            }, IntPtr.Zero);
        }
        catch { /* fall through with whatever was collected */ }

        return found;
    }

    /// <summary>
    /// The chat named in the window title, read at the moment it is needed. A title read
    /// at the start of a sweep can be out of date by the time the box is reached.
    /// </summary>
    private static string CurrentSession(IntPtr window)
    {
        try
        {
            var buffer = new System.Text.StringBuilder(512);
            return GetWindowText(window, buffer, buffer.Capacity) > 0
                ? SessionFromTitle(buffer.ToString())
                : "unknown";
        }
        catch { return "unknown"; }
    }

    /// <summary>
    /// Decides what a new buffer value means. Text still being written overwrites the row
    /// the box owns. Anything else closes that row under its old label first, then the
    /// new text gets the new label.
    /// </summary>
    private void Observe(Tracked t, string text, string currentSession)
    {
        var previousWasReal = !string.IsNullOrWhiteSpace(t.LastText) && !IsPlaceholder(t.LastText);
        var vanished = string.IsNullOrWhiteSpace(text) || IsPlaceholder(text);

        if (t.OwnLabel is not null) currentSession = t.OwnLabel;

        // The subscription moved to another chat's box. However alike the two texts are,
        // even the same, they are different drafts, and the first box leaving the screen
        // is not a send.
        var switched = t.OwnLabel is not null && t.Session != "unknown" && t.Session != t.OwnLabel;
        if (text == t.LastText && !switched) return;

        Log($"observe {t.Owner}: {t.LastText.Length} -> {text.Length} chars, " +
            $"session '{t.Session}' -> '{currentSession}'");

        var continues = !switched && SameDraft(t.LastText, text);

        if ((!continues || vanished) && previousWasReal)
        {
            // The row's own label names the chat this text was written in.
            var finished = t.Pending ?? t.LastText;
            _store.Update(t.Owner, t.Session, finished);
            t.Pending = null;
            _store.Seal(t.Owner);

            // An emptied box is a send or a clear. Only text that shows up in the
            // conversation counts as sent.
            if (vanished && !switched)
            {
                lock (_sendGate)
                    _sendChecks.Add(new SendCheck
                    {
                        Window = t.Window,
                        Scope = t.Document,
                        Session = t.Session,
                        Owner = t.Owner,
                        Text = finished,
                        DueAt = Environment.TickCount64 + 700,
                        Looks = 20
                    });
            }

            // Without a document to name it, the new text's chat waits for the tab strip
            // or title to hold still. Either can change before or after the text.
            t.Session = currentSession;
            t.LabelSettled = t.OwnLabel is not null;
            t.LastTitleSeen = currentSession;
            t.TitleSteadySince = Environment.TickCount64;
        }
        else if (switched || (t.Session == "unknown" && currentSession != "unknown"))
        {
            t.Session = currentSession;
        }

        if (switched) CheckSavedDrafts(t, text);

        var startedFromNothing = !previousWasReal && !vanished;
        t.LastText = text;

        if (vanished)
        {
            t.Pending = null;
            return;
        }

        // Typing into an empty box starts a new draft, the same as a switch. The label
        // settled while the box sat empty can belong to the chat before.
        if (startedFromNothing)
        {
            t.LabelSettled = t.OwnLabel is not null;
            t.LastTitleSeen = currentSession;
            t.TitleSteadySince = Environment.TickCount64;
            if (currentSession != "unknown") t.Session = currentSession;
        }

        t.Pending = text;
        t.PendingAt = Environment.TickCount64;
        Activity?.Invoke(t.Session);
    }

    private sealed class SendCheck
    {
        public required IntPtr Window { get; init; }

        /// <summary>The chat's own document. Only its conversation is searched.</summary>
        public AutomationElement? Scope { get; init; }
        public required string Session { get; init; }
        public string? Owner { get; init; }
        public required string Text { get; init; }
        public required int Looks { get; init; }
        public long DueAt;
        public int Attempts;
    }

    /// <summary>
    /// Opening a chat is a chance to catch a send that was missed. A saved draft whose
    /// text shows in the conversation was sent, and the text is there to copy anyway.
    /// One look each. Text still in the box is skipped. The box's own lines would match it.
    /// </summary>
    private void CheckSavedDrafts(Tracked t, string boxText)
    {
        if (t.Document is null || t.OwnLabel is null) return;
        var due = Environment.TickCount64 + 700;
        lock (_sendGate)
        {
            foreach (var text in _store.FinishedTexts(t.OwnLabel))
            {
                if (boxText.Contains(text, StringComparison.Ordinal)) continue;
                _sendChecks.Add(new SendCheck
                {
                    Window = t.Window,
                    Scope = t.Document,
                    Session = t.OwnLabel,
                    Text = text,
                    DueAt = due,
                    Looks = 1
                });
            }
        }
    }

    private readonly List<SendCheck> _sendChecks = new();
    private readonly object _sendGate = new();

    /// <summary>
    /// Whether a message left the box because it was sent. A sent message shows in the
    /// conversation straight away. This asks whether the exact text is now in the chat's
    /// own document, or in the window when the box has none. The answer is yes or no; the
    /// conversation is not read.
    /// </summary>
    private static bool WasSent(IntPtr window, AutomationElement? scope, string text)
    {
        AutomationElement root;
        try
        {
            var found = scope ?? AutomationElement.FromHandle(window);
            if (found is null) return false;
            root = found;
        }
        catch { return false; }

        // The box and the conversation rarely hold the exact same string: a trailing
        // newline comes and goes, and line endings differ.
        foreach (var candidate in Variants(text))
        {
            try
            {
                var cond = new PropertyCondition(AutomationElement.NameProperty, candidate);
                if (root.FindFirst(TreeScope.Descendants, cond) is not null) return true;
            }
            catch { /* the tree can shift under the query; try the next form */ }
        }
        return false;
    }

    private static IEnumerable<string> Variants(string text)
    {
        // A chat box reports one line break more per blank line than the conversation
        // shows. A blank line comes through as three line breaks; the conversation has two.
        var shown = System.Text.RegularExpressions.Regex.Replace(text.Replace("\r\n", "\n"), "\n(\n+)", "$1");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in new[]
                 {
                     shown,
                     shown.Trim(),
                     shown.TrimEnd(),
                     text,
                     text.TrimEnd('\r', '\n', ' ', '\t'),
                     text.Trim(),
                     text.Replace("\r\n", "\n"),
                     text.Replace("\r\n", "\n").TrimEnd('\r', '\n', ' ', '\t'),
                     text.Replace("\n", "\r\n"),
                     text.Replace("\n", "\r\n").TrimEnd('\r', '\n', ' ', '\t')
                 })
        {
            if (candidate.Length > 0 && seen.Add(candidate)) yield return candidate;
        }
    }

    /// <summary>Drops drafts whose send is confirmed. A draft that cannot be confirmed is kept.</summary>
    private void ProcessSendChecks()
    {
        List<SendCheck> due;
        lock (_sendGate)
            due = _sendChecks.Where(c => Environment.TickCount64 >= c.DueAt).ToList();

        foreach (var check in due)
        {
            if (WasSent(check.Window, check.Scope, check.Text))
            {
                if (_store.ForgetSent(check.Session, check.Text, check.Owner))
                    Log($"confirmed sent, dropped draft ({check.Text.Length} chars) in '{check.Session}'");
                lock (_sendGate) _sendChecks.Remove(check);
                continue;
            }

            check.Attempts++;
            // The conversation takes a moment to render, and leaving the chat right after
            // sending hides it until you come back.
            if (check.Attempts >= check.Looks)
            {
                if (check.Looks > 1) Log($"not confirmed sent after {check.Attempts} looks, keeping draft");
                lock (_sendGate) _sendChecks.Remove(check);
            }
            else
            {
                check.DueAt = Environment.TickCount64 + 1500;
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// Picks up a new box as soon as it has focus. The window search runs every four
    /// seconds, long enough after a switch to miss what you type next.
    /// </summary>
    private void FocusScan()
    {
        if (_disposed || Paused) return;

        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return;

            bool watched;
            lock (_gate) watched = _tracked.Values.Any(t => t.Window == window);
            if (!watched) return;   // the window search finds a window first

            var focused = AutomationElement.FocusedElement;
            if (focused is null) return;

            var name = focused.Current.Name;
            if (Array.IndexOf(BoxNames, name) < 0) return;

            var owner = $"{window}|{name}";
            Tracked? existing;
            lock (_gate) _tracked.TryGetValue(owner, out existing);
            if (existing is null) return;

            var same = false;
            try { same = Automation.Compare(existing.Element, focused); } catch { }
            if (!same)
            {
                Log($"focus found another box for {owner}, moving subscription");
                Swap(existing, focused, owner, window);
                lock (_gate) _tracked.TryGetValue(owner, out existing);
            }

            if (existing is null) return;
            string text;
            try { text = ReadText(existing.Element); }
            catch { return; }
            lock (_gate) Observe(existing, text, CurrentSession(window));
        }
        catch { /* focus moves constantly; a miss here is not worth reporting */ }
    }

    /// <summary>Moves the subscription to the box now in this place, carrying buffer state.</summary>
    private void Swap(Tracked existing, AutomationElement box, string owner, IntPtr window)
    {
        try { Automation.RemoveAutomationPropertyChangedEventHandler(existing.Element, existing.Handler); }
        catch { /* the old node is already gone */ }

        var (document, label) = NamedDocument(box);
        Tracked? self = null;
        var moved = new Tracked
        {
            Window = window,
            Owner = owner,
            Element = box,
            Handler = (_, e) => OnChanged(self!, e),
            Document = document,
            OwnLabel = label,
            Session = existing.Session,
            LastText = existing.LastText,
            Pending = existing.Pending,
            PendingAt = existing.PendingAt
        };
        self = moved;

        try
        {
            Automation.AddAutomationPropertyChangedEventHandler(
                box, TreeScope.Element, moved.Handler, ValuePattern.ValueProperty);
        }
        catch { return; }

        lock (_gate) _tracked[owner] = moved;
    }

    private const int TitleSteadyMs = 1500;

    private static readonly Dictionary<IntPtr, (string Name, long At)> TabCache = new();
    private static readonly object TabCacheGate = new();

    /// <summary>
    /// The selected tab in the editor's tab strip. The window title names the focused
    /// editor, which is a file as often as a chat.
    /// </summary>
    private static string SelectedSessionTab(IntPtr window)
    {
        lock (TabCacheGate)
        {
            if (TabCache.TryGetValue(window, out var hit) && Environment.TickCount64 - hit.At < 750)
                return hit.Name;
        }

        var answer = "unknown";
        try
        {
            var root = AutomationElement.FromHandle(window);
            if (root is not null)
            {
                var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem);
                var tabs = root.FindAll(TreeScope.Descendants, cond);

                for (var i = 0; i < tabs.Count; i++)
                {
                    var tab = tabs[i];
                    try
                    {
                        var pattern = tab.GetCurrentPattern(SelectionItemPattern.Pattern) as SelectionItemPattern;
                        if (pattern is null || !pattern.Current.IsSelected) continue;
                        if (!IsEditorTab(tab)) continue;   // the side bar and panel have tabs too
                        var name = tab.Current.Name;
                        if (!string.IsNullOrWhiteSpace(name)) { answer = name.Trim(); break; }
                    }
                    catch { /* a tab can disappear while the strip re-renders */ }
                }
            }
        }
        catch { /* fall back to the title */ }

        lock (TabCacheGate) TabCache[window] = (answer, Environment.TickCount64);
        return answer;
    }

    /// <summary>
    /// Whether a tab sits in the editor's tab strip ("tabs-container"). The side bar's
    /// view switcher ("actions-container") also has a selected tab.
    /// </summary>
    private static bool IsEditorTab(AutomationElement tab)
    {
        try
        {
            var parent = TreeWalker.ControlViewWalker.GetParent(tab);
            return parent?.Current.ClassName == "tabs-container";
        }
        catch { return false; }
    }

    /// <summary>
    /// Names the chat of a new buffer once the tab strip or title has held the same value
    /// for a moment. Either can change before the text does. A settled label stays.
    /// </summary>
    private void SettleLabel(Tracked t)
    {
        // A box with a named document already knows its chat, and Observe keeps the label
        // in step. Relabelling here could hand one chat's live row to another.
        if (t.OwnLabel is not null)
        {
            lock (_gate) t.LabelSettled = true;
            return;
        }

        // Read through the editor before taking the lock. A slow editor then holds up only this.
        var title = SelectedSessionTab(t.Window);
        if (title == "unknown") title = CurrentSession(t.Window);

        lock (_gate)
        {
            if (t.LabelSettled) return;

            if (title != t.LastTitleSeen)
            {
                t.LastTitleSeen = title;
                t.TitleSteadySince = Environment.TickCount64;
                return;
            }

            if (Environment.TickCount64 - t.TitleSteadySince < TitleSteadyMs) return;
            if (title == "unknown") return;

            if (t.Session != title)
            {
                Log($"label settled for {t.Owner}: '{t.Session}' -> '{title}'");
                t.Session = title;
                if (t.Pending is null) _store.Relabel(t.Owner, title);
            }
            t.LabelSettled = true;
        }
    }

    /// <summary>Writes a pending buffer once typing has paused, over the row the box owns.</summary>
    private void Flush()
    {
        if (_disposed || Paused) return;
        lock (_gate)
        {
            foreach (var t in _tracked.Values)
            {
                if (t.Pending is null) continue;
                if (Environment.TickCount64 - t.PendingAt < QuietMs) continue;
                _store.Update(t.Owner, t.Session, t.Pending);
                t.Pending = null;
            }
        }
    }

    /// <summary>The work that reads through the editor: focus, sends and tab labels.</summary>
    private void Check()
    {
        if (_disposed || Paused) return;
        FocusScan();
        ProcessSendChecks();

        List<Tracked> unsettled;
        lock (_gate) unsettled = _tracked.Values.Where(t => !t.LabelSettled).ToList();
        foreach (var t in unsettled) SettleLabel(t);
    }

    /// <summary>Backstop in case an event is dropped. One property read, no tree walk.</summary>
    private void Poll(IntPtr handle)
    {
        lock (_gate)
        {
            foreach (var kv in _tracked.Where(kv => kv.Value.Window == handle).ToList())
            {
                try { Observe(kv.Value, ReadText(kv.Value.Element), CurrentSession(handle)); }
                catch { Log($"poll read failed for {kv.Key}, retiring"); Retire(kv.Key); }
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _discovery.Dispose();
        _flush.Dispose();
        _checks.Dispose();
        lock (_gate)
        {
            foreach (var key in _tracked.Keys.ToList()) Retire(key);
        }
    }
}
