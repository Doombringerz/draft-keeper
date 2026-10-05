using System.Windows.Forms;

namespace DraftKeeperTestHost;

/// <summary>
/// Stands in for an editor window so capture can be exercised end to end without
/// typing into a real chat.
///
/// The first half swaps the contents of one text box while the window title changes in
/// the same instant. Text going away belongs to the chat that was on screen while it was
/// typed.
///
/// The second half shows chats the way VS Code does, each in its own document named
/// after its tab, and reopens them the way a chat puts its draft back in the box.
///
/// tests\run.ps1 runs this and lists what has to have been kept.
/// </summary>
internal static class Program
{
    private const string Placeholder = "Queue another message…";

    private static Form _form = null!;
    private static TextBox _box = null!;   // replaced when the box is rebuilt
    private static Label _log = null!;
    private static Label _transcript = null!;
    private static readonly Queue<Action> Steps = new();

    private static void Title(string session)
        => _form.Text = $"{session} - test-workspace (Workspace) - Visual Studio Code";

    private static void Pause(int ticks, string label)
    {
        for (var i = 0; i < ticks; i++) Steps.Enqueue(() => _log.Text = label);
    }

    private static void TypeOut(string text, string label, int from = 1)
    {
        for (var i = from; i <= text.Length; i++)
        {
            var slice = text[..i];
            Steps.Enqueue(() => { _box.Text = slice; _log.Text = label; });
        }
    }

    private static Panel? _tab;
    private static Label _said = null!;   // the open chat's conversation

    /// <summary>
    /// Shows a chat the way VS Code does: its own document, named after its tab, holding
    /// its own box and conversation. Only the open chat is in the window.
    /// </summary>
    private static void OpenTab(string session, string text, string conversation = "")
    {
        if (_tab is not null)
        {
            _form.Controls.Remove(_tab);
            _tab.Dispose();
        }
        _tab = new Panel { Dock = DockStyle.Fill, AccessibleName = session, AccessibleRole = AccessibleRole.Document };
        _box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AccessibleName = "Message input",
            AccessibleRole = AccessibleRole.Text,
            Text = text
        };
        _said = new Label { Dock = DockStyle.Top, Height = 40, Text = conversation };
        _tab.Controls.Add(_box);
        _tab.Controls.Add(_said);
        _form.Controls.Add(_tab);
        _tab.BringToFront();
        _form.Activate();
        _box.Focus();
    }

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        _form = new Form { Width = 780, Height = 260 };
        _box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AccessibleName = "Message input",
            AccessibleRole = AccessibleRole.Text,
            Text = Placeholder
        };
        _log = new Label { Dock = DockStyle.Bottom, Height = 26, Text = "waiting" };
        // Stands in for the conversation. A sent message shows up here; a cleared one does not.
        _transcript = new Label { Dock = DockStyle.Top, Height = 40, Text = string.Empty };
        _form.Controls.Add(_box);
        _form.Controls.Add(_transcript);
        _form.Controls.Add(_log);
        Title("Session Alpha");
        _form.Shown += (_, _) => { _form.Activate(); _box.Focus(); };

        /* Five sent messages in Alpha. Only the newest three may survive. */
        for (var n = 1; n <= 5; n++)
        {
            var number = n;
            var text = $"Alpha message {number}. Sent, and kept as history under Session Alpha.";
            if (number == 5) TypeOut(text, $"typing alpha {number}");
            else Steps.Enqueue(() => { _box.Text = text; _log.Text = $"alpha {number}"; });
            Pause(18, $"pause after alpha {number}");
            Steps.Enqueue(() => { _box.Text = Placeholder; _log.Text = $"alpha {number} sent"; });
            Pause(10, "placeholder must not be saved");
        }

        /* An unsent draft left in the box, then a tab switch. Title and text change
           together. */
        const string alphaUnsent = "Alpha unsent draft left in the box when the tab was switched away.";
        Steps.Enqueue(() => { _box.Text = alphaUnsent; _log.Text = "alpha unsent"; });
        Pause(18, "pause, alpha unsent pending");

        /* An edit in the middle. The new text shares a long prefix and a long suffix
           with the old one without being a prefix match. It must stay one draft. */
        const string alphaEdited = "Alpha unsent draft left in the EDITED MIDDLE when the tab was switched away.";
        Steps.Enqueue(() => { _box.Text = alphaEdited; _log.Text = "alpha edited mid-text"; });
        Pause(8, "pause after middle edit");

        /* Backspacing a tail, then retyping it. */
        Steps.Enqueue(() => { _box.Text = alphaEdited[..48]; _log.Text = "alpha backspaced"; });
        Pause(6, "pause after backspace");
        Steps.Enqueue(() => { _box.Text = alphaEdited; _log.Text = "alpha retyped"; });
        Pause(18, "pause, alpha unsent pending");

        const string betaUnsent = "Beta unsent draft. This one belongs to Session Beta and nothing else.";
        Steps.Enqueue(() =>
        {
            Title("Session Beta");
            _box.Text = betaUnsent;
            _log.Text = "switched to beta";
        });
        Pause(60, "pause, beta unsent pending");

        /* A chat panel rebuilds its input control on a chat switch. Anything holding
           the old element points at a node that is gone, and can keep reporting the text
           it last had without failing. Destroy and recreate the box here. */
        const string gammaUnsent = "Gamma unsent draft.\r\nSecond line here.\r\n\r\nFourth line after a blank.";
        Steps.Enqueue(() =>
        {
            Title("Session Gamma");
            _form.Controls.Remove(_box);
            _box.Dispose();
            _box = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                AccessibleName = "Message input",
                AccessibleRole = AccessibleRole.Text,
                Text = gammaUnsent
            };
            _form.Controls.Add(_box);
            _box.BringToFront();
            _form.Activate();
            _box.Focus();
            _log.Text = "switched to gamma, box rebuilt";
        });
        Pause(110, "pause, gamma unsent pending");

        /* A fourth chat after the rebuild. Capture has to still be running. */
        const string deltaUnsent = "Delta unsent draft, typed after the input control was rebuilt once already.";
        Steps.Enqueue(() =>
        {
            Title("Session Delta");
            _box.Text = deltaUnsent;
            _log.Text = "switched to delta";
        });
        Pause(110, "pause, delta unsent pending");

        /* Switching to a tab that already holds an unsent draft, with the contents
           arriving before the title catches up. The incoming text belongs to the tab
           being opened. */
        const string epsilonExisting = "Epsilon draft that was already sitting in that tab before it was opened.";
        Steps.Enqueue(() =>
        {
            _box.Text = epsilonExisting;          // contents first
            _log.Text = "contents swapped, title still Delta";
        });
        Steps.Enqueue(() => { Title("Session Epsilon"); _log.Text = "title caught up"; });
        Pause(110, "pause, epsilon pending");

        /* A hint string an empty box shows. It is not a draft and must never be saved. */
        Steps.Enqueue(() =>
        {
            Title("Session Zeta");
            _box.Text = "ctrl esc to focus or unfocus Claude";
            _log.Text = "hint text must not be saved";
        });
        Pause(60, "hint must not be saved");

        /* A send. The box empties and the message appears in the conversation. That
           draft must be dropped. */
        const string sentText = "Theta message that is sent and must not be kept as a draft.";
        Steps.Enqueue(() =>
        {
            Title("Session Theta");
            _box.Text = sentText;
            _log.Text = "theta typed";
        });
        Pause(60, "pause, theta pending");
        Steps.Enqueue(() =>
        {
            _transcript.Text = sentText;          // it now shows in the conversation
            _box.Text = Placeholder;              // and the box empties
            _log.Text = "theta SENT";
        });
        Pause(110, "theta should be dropped");

        /* A wipe. The box empties and nothing appears in the conversation. That draft
           must survive. */
        const string wipedText = "Iota message that is only cleared out of the box and must be kept.";
        Steps.Enqueue(() =>
        {
            Title("Session Iota");
            _box.Text = wipedText;
            _log.Text = "iota typed";
        });
        Pause(60, "pause, iota pending");
        Steps.Enqueue(() =>
        {
            _box.Text = Placeholder;              // cleared, nothing sent
            _log.Text = "iota WIPED";
        });
        Pause(140, "iota must be kept");

        /* Chats in editor tabs. Each is its own document named after its tab, and only
           the open one is in the window. The title trails the switch on purpose. The
           label has to come from the document. */
        const string kappa = "Kappa draft. Continue. Where does the restore test stand, and the backup job?";
        const string lambda = "Lambda draft. Continue. Where does the restore test stand, and the backup job?";
        const string kappaMore = " Also read the logs from last night.";

        Steps.Enqueue(() =>
        {
            _form.Controls.Remove(_box);
            _box.Dispose();
            _form.Controls.Remove(_transcript);
            OpenTab("Session Kappa", Placeholder);
            Title("Session Kappa");
            _log.Text = "kappa opened";
        });
        Pause(20, "kappa empty");
        TypeOut(kappa, "typing kappa");
        Pause(40, "pause, kappa pending");

        /* Another chat holding nearly the same text, open before the title changes. */
        Steps.Enqueue(() => { OpenTab("Session Lambda", lambda); _log.Text = "lambda open, title still kappa"; });
        Pause(40, "title trailing");
        Steps.Enqueue(() => { Title("Session Lambda"); _log.Text = "title caught up"; });
        Pause(40, "pause, lambda pending");

        /* Back to Kappa. The chat puts its draft back in the box, and that must not make a copy. */
        Steps.Enqueue(() => { OpenTab("Session Kappa", kappa); Title("Session Kappa"); _log.Text = "kappa reopened"; });
        Pause(60, "kappa must not be copied");
        TypeOut(kappa + kappaMore, "typing more in kappa", kappa.Length + 1);
        Pause(40, "pause, kappa pending");

        /* Leaving Lambda for a chat whose conversation already shows Lambda's text, as if
           the same message had been sent there. Leaving must not count as sending. */
        Steps.Enqueue(() => { OpenTab("Session Lambda", lambda); Title("Session Lambda"); _log.Text = "lambda reopened"; });
        Pause(40, "pause, lambda open");
        Steps.Enqueue(() =>
        {
            OpenTab("Session Mu", Placeholder, conversation: lambda);
            Title("Session Mu");
            _log.Text = "mu open, showing lambda's text";
        });
        Pause(160, "lambda must be kept");

        /* A sent message with a blank line. The box reports three line breaks for it and
           the conversation shows two, the way a chat renders it. */
        const string omicronTyped = "Omicron message.\r\n\r\n\r\nSecond paragraph after a blank line.";
        const string omicronShown = "Omicron message.\n\nSecond paragraph after a blank line.";
        Steps.Enqueue(() => { OpenTab("Session Omicron", Placeholder); Title("Session Omicron"); _log.Text = "omicron opened"; });
        Pause(20, "omicron empty");
        Steps.Enqueue(() => { _box.Text = omicronTyped; _log.Text = "omicron typed"; });
        Pause(40, "pause, omicron pending");
        Steps.Enqueue(() => { _said.Text = omicronShown; _box.Text = Placeholder; _log.Text = "omicron SENT"; });
        Pause(110, "omicron should be dropped");

        /* A chat whose conversation already shows one of its saved drafts, sent while
           nothing was watching. Opening it drops that draft and keeps the other. */
        const string piSent = "Pi message that was sent before this program could confirm it.";
        Steps.Enqueue(() => { OpenTab("Session Pi", Placeholder, conversation: piSent); Title("Session Pi"); _log.Text = "pi opened"; });
        Pause(110, "pi's sent draft should be dropped");

        Steps.Enqueue(() => { _log.Text = "done"; Application.Exit(); });

        var timer = new System.Windows.Forms.Timer { Interval = 45 };
        timer.Tick += (_, _) => { if (Steps.Count > 0) Steps.Dequeue()(); };
        timer.Start();

        Application.Run(_form);
    }
}
