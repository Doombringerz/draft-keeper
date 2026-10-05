using System.Windows.Forms;

namespace DraftKeeperTestHost;

// A fake editor window. Tests the whole thing without typing into a real chat.
// First half: one text box, contents and title swapped at the same moment.
// Second half: chats like VS Code shows them, one web view per tab, reopened with their
// draft still in the box. tests\run.ps1 runs it and checks what got kept.
internal static class Program
{
    private const string Placeholder = "Queue another message…";

    private static Form _form = null!;
    private static TextBox _box = null!;   // swapped out when the box gets rebuilt
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

    // One chat like VS Code does it. Its own view named after the tab, its own box and
    // conversation, and only the open one in the window.
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
        // The conversation. Sent messages show up here, cleared ones don't.
        _transcript = new Label { Dock = DockStyle.Top, Height = 40, Text = string.Empty };
        _form.Controls.Add(_box);
        _form.Controls.Add(_transcript);
        _form.Controls.Add(_log);
        Title("Session Alpha");
        _form.Shown += (_, _) => { _form.Activate(); _box.Focus(); };

        // Five sent messages in Alpha. Only the newest three get kept.
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

        // Unsent draft left in the box, then a tab switch. Title and text change together.
        const string alphaUnsent = "Alpha unsent draft left in the box when the tab was switched away.";
        Steps.Enqueue(() => { _box.Text = alphaUnsent; _log.Text = "alpha unsent"; });
        Pause(18, "pause, alpha unsent pending");

        // Edit in the middle. Has to stay one draft.
        const string alphaEdited = "Alpha unsent draft left in the EDITED MIDDLE when the tab was switched away.";
        Steps.Enqueue(() => { _box.Text = alphaEdited; _log.Text = "alpha edited mid-text"; });
        Pause(8, "pause after middle edit");

        // Backspace the end, type it again.
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

        // Chat panels rebuild their box when you switch. The old element is dead but can
        // keep reporting its last text. So: destroy the box and make a new one.
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

        // Fourth chat after the rebuild. Still capturing?
        const string deltaUnsent = "Delta unsent draft, typed after the input control was rebuilt once already.";
        Steps.Enqueue(() =>
        {
            Title("Session Delta");
            _box.Text = deltaUnsent;
            _log.Text = "switched to delta";
        });
        Pause(110, "pause, delta unsent pending");

        // Switch to a tab that already has a draft, text first, title after. The text
        // belongs to the tab you opened.
        const string epsilonExisting = "Epsilon draft that was already sitting in that tab before it was opened.";
        Steps.Enqueue(() =>
        {
            _box.Text = epsilonExisting;          // contents first
            _log.Text = "contents swapped, title still Delta";
        });
        Steps.Enqueue(() => { Title("Session Epsilon"); _log.Text = "title caught up"; });
        Pause(110, "pause, epsilon pending");

        // Key hint an empty box shows. Never saved.
        Steps.Enqueue(() =>
        {
            Title("Session Zeta");
            _box.Text = "ctrl esc to focus or unfocus Claude";
            _log.Text = "hint text must not be saved";
        });
        Pause(60, "hint must not be saved");

        // A send. Box empties, message shows up in the conversation. Draft goes.
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
            _transcript.Text = sentText;          // shows up in the conversation
            _box.Text = Placeholder;              // and the box empties
            _log.Text = "theta SENT";
        });
        Pause(110, "theta should be dropped");

        // A wipe. Box empties, nothing in the conversation. Draft stays.
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

        // Second half, chats in editor tabs. The title lags behind on purpose, the chat
        // name has to come from the tab's own view.
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

        // Another chat with almost the same text, open before the title changes.
        Steps.Enqueue(() => { OpenTab("Session Lambda", lambda); _log.Text = "lambda open, title still kappa"; });
        Pause(40, "title trailing");
        Steps.Enqueue(() => { Title("Session Lambda"); _log.Text = "title caught up"; });
        Pause(40, "pause, lambda pending");

        // Back to Kappa. Its draft comes back in the box. No copy allowed.
        Steps.Enqueue(() => { OpenTab("Session Kappa", kappa); Title("Session Kappa"); _log.Text = "kappa reopened"; });
        Pause(60, "kappa must not be copied");
        TypeOut(kappa + kappaMore, "typing more in kappa", kappa.Length + 1);
        Pause(40, "pause, kappa pending");

        // Leave Lambda for a chat that already shows Lambda's text, like the same message
        // got sent there before. Leaving isn't sending.
        Steps.Enqueue(() => { OpenTab("Session Lambda", lambda); Title("Session Lambda"); _log.Text = "lambda reopened"; });
        Pause(40, "pause, lambda open");
        Steps.Enqueue(() =>
        {
            OpenTab("Session Mu", Placeholder, conversation: lambda);
            Title("Session Mu");
            _log.Text = "mu open, showing lambda's text";
        });
        Pause(160, "lambda must be kept");

        // Sent message with a blank line. Three line breaks in the box, two in the
        // conversation, same as the real thing.
        const string omicronTyped = "Omicron message.\r\n\r\n\r\nSecond paragraph after a blank line.";
        const string omicronShown = "Omicron message.\n\nSecond paragraph after a blank line.";
        Steps.Enqueue(() => { OpenTab("Session Omicron", Placeholder); Title("Session Omicron"); _log.Text = "omicron opened"; });
        Pause(20, "omicron empty");
        Steps.Enqueue(() => { _box.Text = omicronTyped; _log.Text = "omicron typed"; });
        Pause(40, "pause, omicron pending");
        Steps.Enqueue(() => { _said.Text = omicronShown; _box.Text = Placeholder; _log.Text = "omicron SENT"; });
        Pause(110, "omicron should be dropped");

        // A chat that already shows one of its saved drafts, sent while nothing was
        // watching. Opening it drops that one and keeps the other.
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
