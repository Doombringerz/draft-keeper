using System.Diagnostics;
using System.Windows.Automation;

namespace DraftKeeper;

// Watches the Claude Code chat box in every editor window. Finds the boxes by name every
// few seconds, then listens for text changes. No keyboard hook.
// Password fields come through as bullets. Passwords never get saved.
public sealed class InputWatcher : IDisposable
{
    private static readonly string[] BoxNames = { "Message input", "Ask a side question" };

    // What an empty box reports as its text. Not drafts.
    private static readonly string[] Placeholders =
    {
        "queue another message", "type your message", "ask a side question",
        "message", "type a message", "send a message", "ask claude anything",
        "reply to claude", "what would you like to do"
    };

    // Key hints an empty box shows instead. The wording changes between versions, hence the fragments.
    private static readonly string[] HintFragments =
    {
        "to focus or unfocus", "press enter to send", "shift+enter", "shift + enter",
        "for newline", "for a new line", "to send a message", "esc to"
    };

    private const int QuietMs = 900;

    // Keep this above ProcessNames. Static fields initialise top to bottom, and swapping them crashes on start.
    private static readonly string[] BuiltInEditors = { "Code", "Code - Insiders", "Cursor", "VSCodium", "Windsurf" };

    // DRAFTKEEPER_EXTRA_PROCESSES adds more, comma separated.
    private static readonly string[] ProcessNames = BuildProcessNames();

    private static string[] BuildProcessNames()
    {
        var names = new List<string>(BuiltInEditors);
        var extra = Environment.GetEnvironmentVariable("DRAFTKEEPER_EXTRA_PROCESSES");
        if (!string.IsNullOrWhiteSpace(extra))
            names.AddRange(extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    // DRAFTKEEPER_LOG=<file> logs what the watcher decides. Chat names and lengths, never the text.
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
        catch { /* a broken log never breaks capture */ }
    }

    private readonly DraftStore _store;
    private readonly System.Threading.Timer _discovery;
    private readonly System.Threading.Timer _flush;
    private readonly System.Threading.Timer _checks;
    private readonly object _gate = new();
    private readonly Dictionary<string, Tracked> _tracked = new();

    private bool _disposed;

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

        public string Session = "unknown";

        // The chat's own web view, when the box sits in one. Its name is the tab's name.
        public AutomationElement? Document;
        public string? OwnLabel;

        // Only used without a web view. The tab strip or title has to sit still for a moment
        // first. It can change before or after the text does.
        public bool LabelSettled;
        public string LastTitleSeen = string.Empty;
        public long TitleSteadySince;
    }

    private int _sweeping;
    private int _flushing;
    private int _checking;

    // Saving gets its own timer and never touches the editor. A busy VS Code can stall a
    // search for minutes, and saving used to stall right along with it.
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

    // Timers don't wait for the last run to finish. One run at a time, or slow runs pile up.
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
        return t.Length <= 80 && HintFragments.Any(h => t.Contains(h, StringComparison.Ordinal));
    }

    // Same draft if you typed on, backspaced, or edited somewhere in the middle.
    internal static bool SameDraft(string before, string after)
    {
        if (before.Length == 0 || after.Length == 0) return false;

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

        // Edited in the middle: at least half the text is still the same.
        return (prefix + suffix) * 2 >= max;
    }

    private static string SessionFromTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "unknown";
        // "Ledger - my-workspace (Workspace) - Visual Studio Code" -> "Ledger"
        var cut = title.IndexOf(" - ", StringComparison.Ordinal);
        var head = (cut > 0 ? title[..cut] : title).TrimStart('●', '*', ' ').Trim();

        // Sometimes the title is just the first line of a long prompt. Not a tab name.
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

            // Every sweep, every window. A replaced box can leave a dead element behind that
            // keeps reporting its old text instead of failing.
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
            // Happens. The window changes mid-sweep. Next sweep is four seconds away.
            Log($"SWEEP THREW {ex.GetType().Name}: {ex.Message}");
        }
    }

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
            // Searching while the chat rebuilds can hit a node that just died. Retry. That's
            // exactly when the new box shows up.
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

                    // Different box in the same spot. Either the chat rebuilt it or you
                    // switched tabs. Observe works out which from the box's web view.
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

                // Whatever's already in the box counts too. Say after a crash, or typed while
                // Draft Keeper wasn't running.
                try
                {
                    var current = ReadText(box);
                    lock (_gate) Observe(tracked, current, tracked.Session);
                    CheckSavedDrafts(tracked, current);
                }
                catch { /* chat reloaded during the first read */ }
            }

            // Boxes that weren't found are gone. Drop them, or one box ends up tracked twice
            // once a second chat is on screen.
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

    // In VS Code every chat tab is its own web view, named after the tab. Reading the name
    // from the box's own view means a draft can't end up under the wrong chat.
    // No view around the box? Then it's null and the tab strip or title has to do.
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
        catch { /* tab closed mid-walk */ }
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

    // Take the text the event carries. Reading the box again gets whatever's in it by now,
    // and a box that was empty for half a second between two messages gets missed.
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
            // Late event from a box that's already been swapped out. That text isn't this chat's.
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

    // All windows, not MainWindowHandle. Electron runs every window from one process and
    // MainWindowHandle only knows one of them.
    private static List<IntPtr> EditorWindows()
    {
        // The window class filters out Electron's tooltips and helper windows. Extra
        // editors from DRAFTKEEPER_EXTRA_PROCESSES skip that check.
        var electron = new HashSet<int>();
        var anyClass = new HashSet<int>();
        foreach (var name in ProcessNames)
        {
            var target = BuiltInEditors.Contains(name, StringComparer.OrdinalIgnoreCase) ? electron : anyClass;
            try
            {
                foreach (var proc in Process.GetProcessesByName(name)) target.Add(proc.Id);
            }
            catch { /* exited while listing */ }
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
                catch { /* window closed mid-list */ }
                return true;
            }, IntPtr.Zero);
        }
        catch { /* keep what's been found */ }

        return found;
    }

    // Read the title right when it's needed. Read at the start of a sweep, it can already be
    // the next chat by the time the box gets checked.
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

    // Still the same draft? Overwrite its row. Something else? Close the old row under the
    // old chat first, then the new text gets the new chat.
    private void Observe(Tracked t, string text, string currentSession)
    {
        var previousWasReal = !string.IsNullOrWhiteSpace(t.LastText) && !IsPlaceholder(t.LastText);
        var vanished = string.IsNullOrWhiteSpace(text) || IsPlaceholder(text);

        if (t.OwnLabel is not null) currentSession = t.OwnLabel;

        // Moved to another chat's box. Two chats with the same text are still two drafts,
        // and leaving a chat isn't sending.
        var switched = t.OwnLabel is not null && t.Session != "unknown" && t.Session != t.OwnLabel;
        if (text == t.LastText && !switched) return;

        Log($"observe {t.Owner}: {t.LastText.Length} -> {text.Length} chars, " +
            $"session '{t.Session}' -> '{currentSession}'");

        var continues = !switched && SameDraft(t.LastText, text);

        if ((!continues || vanished) && previousWasReal)
        {
            var finished = t.Pending ?? t.LastText;
            _store.Update(t.Owner, t.Session, finished);
            t.Pending = null;
            _store.Seal(t.Owner);

            // Box went empty. Sent, or did you clear it? Only counts as sent once it shows up
            // in the conversation.
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

        // Typing into an empty box is a new draft. Its label might still be the last chat's.
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

        public AutomationElement? Scope { get; init; }   // the chat's own web view
        public required string Session { get; init; }
        public string? Owner { get; init; }
        public required string Text { get; init; }
        public required int Looks { get; init; }
        public long DueAt;
        public int Attempts;
    }

    // Opening a chat catches sends that slipped through. If a saved draft is in the
    // conversation, it got sent, and you can copy it from there anyway.
    // Skips whatever's still in the box. The box's own lines would match.
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

    // Is this exact text in the chat now? Windows answers yes or no. The conversation
    // itself never gets read.
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

        // Box and conversation almost never match exactly. Trailing newlines, line endings.
        foreach (var candidate in Variants(text))
        {
            try
            {
                var cond = new PropertyCondition(AutomationElement.NameProperty, candidate);
                if (root.FindFirst(TreeScope.Descendants, cond) is not null) return true;
            }
            catch { /* window changed mid-search, try the next one */ }
        }
        return false;
    }

    private static IEnumerable<string> Variants(string text)
    {
        // The box reports an extra line break per blank line. Three in the box, two in the
        // conversation. Without this, nearly every sent message counted as unsent.
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

    // Not confirmed means kept. Losing a draft you only cleared is worse than keeping one you sent.
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
            // Keep looking for half a minute. The message takes a moment to show up, and if
            // you leave the chat right after sending it's hidden until you're back.
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

    // Picks up a new box the moment you click into it. Waiting four seconds for the next
    // search is long enough to miss what you type after switching.
    private void FocusScan()
    {
        if (_disposed || Paused) return;

        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return;

            bool watched;
            lock (_gate) watched = _tracked.Values.Any(t => t.Window == window);
            if (!watched) return;   // new windows come from the sweep

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
        catch { /* focus moved on, next tick */ }
    }

    private void Swap(Tracked existing, AutomationElement box, string owner, IntPtr window)
    {
        try { Automation.RemoveAutomationPropertyChangedEventHandler(existing.Element, existing.Handler); }
        catch { /* old box already gone */ }

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

    // Selected tab in the editor's tab strip. The window title names whatever has focus,
    // which is a file half the time.
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
                    catch { /* tab vanished while the strip redrew */ }
                }
            }
        }
        catch { /* the title will do */ }

        lock (TabCacheGate) TabCache[window] = (answer, Environment.TickCount64);
        return answer;
    }

    // The side bar's view switcher has a selected tab too, inside "actions-container".
    // Editor tabs sit in "tabs-container".
    private static bool IsEditorTab(AutomationElement tab)
    {
        try
        {
            var parent = TreeWalker.ControlViewWalker.GetParent(tab);
            return parent?.Current.ClassName == "tabs-container";
        }
        catch { return false; }
    }

    // No web view to go on, so wait until the tab or title sits still for a moment.
    // It can change before or after the text. Once settled it stays.
    private void SettleLabel(Tracked t)
    {
        // Box has its own web view. Chat's already known. Relabelling here could move one
        // chat's draft to another.
        if (t.OwnLabel is not null)
        {
            lock (_gate) t.LabelSettled = true;
            return;
        }

        // Ask the editor first, lock after. A slow editor only holds up this bit.
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

    // Saves once you stop typing for a moment.
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

    // Everything that has to ask the editor something.
    private void Check()
    {
        if (_disposed || Paused) return;
        FocusScan();
        ProcessSendChecks();

        List<Tracked> unsettled;
        lock (_gate) unsettled = _tracked.Values.Where(t => !t.LabelSettled).ToList();
        foreach (var t in unsettled) SettleLabel(t);
    }

    // In case an event got dropped. Reads the box, nothing else.
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
