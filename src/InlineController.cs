using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace LazyReply
{
    // Inline translation: the user types in the chat box itself. When a sentence ends (or typing
    // pauses for long), the text of the focused box is read and sent for translation; the three
    // versions appear just above the box. Alt+1/2/3 (or a click) replaces the box text with that version. With Ctrl held, Down / Up
    // walk through the versions, each going into the box at once; Enter (still with Ctrl, or after
    // letting go) sends the one on show.
    // It only reads anything while it is open; it stays open until the user clicks its "×".
    class InlineController
    {
        public event Action ActiveChanged;

        readonly Settings settings;
        readonly AvroWatcher avro;
        readonly InlinePopup popup;
        readonly System.Windows.Forms.Timer debounce, focusWatch;
        readonly uint myPid;

        CancellationTokenSource cts;
        Suggestions current;
        string currentText, inflightText, lastInserted;
        Rectangle anchor;
        IntPtr lastFg;
        int pending = -1, selected = -1;
        bool busy;

        public bool Active { get; private set; }

        // The user paused LazyReply in the app or website in front: the box stays open, but it
        // reads and shows nothing there. It comes back when the user types in a place that is not paused.
        bool suspended;
        public bool Suspended
        {
            get { return suspended; }
            set
            {
                if (suspended == value) return;
                suspended = value;
                if (!value) return;
                debounce.Stop();
                Cancel();
                Captures(false);
                current = null;
                currentText = null;
                popup.HidePopup();
            }
        }

        public InlineController(Settings settings, AvroWatcher avro, uint myPid)
        {
            this.settings = settings;
            this.avro = avro;
            this.myPid = myPid;
            popup = new InlinePopup();
            popup.RowClicked += Accept;
            popup.CloseClicked += Deactivate;
            debounce = new System.Windows.Forms.Timer();
            debounce.Tick += delegate { debounce.Stop(); Refresh(false); };
            focusWatch = new System.Windows.Forms.Timer { Interval = 300 };
            focusWatch.Tick += OnFocusWatch;
            avro.Typed += OnTyped;
            avro.AltKey += delegate(int n) { if (n >= 1) Accept(n - 1); };
            avro.CtrlArrow += Step;
            avro.CtrlEnter += SendChosen;
        }

        // Opens the box at the focused chat box; when it is already open, brings it back into view.
        public void Open()
        {
            if (!Active) { Activate(); return; }
            lastFg = Native.GetForegroundWindow();
            Refresh(true);
        }

        public void Activate()
        {
            if (Active) return;
            Active = true;
            current = null;
            currentText = null;
            lastInserted = null;
            popup.Forget();
            lastFg = Native.GetForegroundWindow();
            focusWatch.Start();
            Log.Write("inline on");
            if (ActiveChanged != null) ActiveChanged();
            Refresh(true);
        }

        public void Deactivate()
        {
            if (!Active) return;
            Active = false;
            debounce.Stop();
            focusWatch.Stop();
            Cancel();
            Captures(false);
            popup.HidePopup();
            Log.Write("inline off");
            if (ActiveChanged != null) ActiveChanged();
        }

        // Keys are taken from the chat app only while they mean something to us. Ctrl+Up/Down only
        // when there are versions to walk through: otherwise they keep their normal meaning.
        void Captures(bool on)
        {
            avro.CaptureAltDigits = on;
            avro.CaptureCtrlArrows = on && current != null;
        }

        void Cancel()
        {
            if (cts != null) cts.Cancel();
            cts = null;
            busy = false;
            inflightText = null;
            pending = -1;
        }

        bool ForegroundIsOurs { get { return Native.ProcessOf(Native.GetForegroundWindow()) == myPid; } }

        // Command prompt and PowerShell windows are left alone. The answer is kept per window,
        // so the process is not looked up again on every key.
        IntPtr termHwnd;
        bool termIs;
        bool ForegroundIsTerminal
        {
            get
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg != termHwnd) { termHwnd = fg; termIs = Native.IsTerminal(fg); }
                return termIs;
            }
        }

        void OnTyped(bool sentenceEnded)
        {
            if (!Active || suspended || ForegroundIsOurs || ForegroundIsTerminal) return;
            debounce.Stop();
            // A "।", "?", "!" or "." ends the sentence: ask at once (the short wait lets the character reach the box).
            // Without one, only a long pause asks: a short pause mid-sentence is thinking, not a finished
            // message. The long pause is for messages without a full stop, such as "ok bhai".
            debounce.Interval = sentenceEnded ? 150 : 3000;
            // The user changed the text: Ctrl+Up/Down/Enter must not put an old version over it.
            avro.CaptureCtrlArrows = false;
            debounce.Start();
            popup.MarkStale();
        }

        // Another window came to the front: the suggestions belong to the old chat box. The box
        // stays open and comes back above the new chat box when the user types there.
        void OnFocusWatch(object sender, EventArgs e)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == lastFg || fg == IntPtr.Zero || Native.ProcessOf(fg) == myPid) return;
            lastFg = fg;
            Cancel();
            Captures(false);
            current = null;
            currentText = null;
            popup.HidePopup();
        }

        Rectangle FallbackAnchor()
        {
            IntPtr fg = Native.GetForegroundWindow();
            Rectangle caret = Native.CaretRect(fg);
            if (caret != Rectangle.Empty) return new Rectangle(caret.Left - 20, caret.Top - 4, 420, caret.Height + 8);
            Rectangle win = Native.WindowRect(fg);
            if (win != Rectangle.Empty) return new Rectangle(win.Left + 40, win.Bottom - 100, Math.Max(420, win.Width - 80), 60);
            return new Rectangle(Cursor.Position, new Size(420, 30));
        }

        // The fourth row, the Bangla translation of English text, is there only when it was asked for and given.
        static string[] Rows(Suggestions s)
        {
            return string.IsNullOrEmpty(s.Bangla)
                ? new[] { s.Professional, s.Friendly, s.Short }
                : new[] { s.Professional, s.Friendly, s.Short, s.Bangla };
        }

        async void Refresh(bool justSwitchedOn)
        {
            FocusReader.Info info = await FocusReader.ReadAsync(1500);
            if (!Active || suspended) return;
            if (ForegroundIsTerminal) { popup.HidePopup(); return; }
            if (!info.Usable || info.ProcessId == myPid)
            {
                if (justSwitchedOn) popup.ShowHint(FallbackAnchor());   // otherwise leave the box where it is
                return;
            }
            anchor = info.Bounds.IsEmpty ? FallbackAnchor() : info.Bounds;
            string text = info.Text.Trim();

            if (text.Length == 0)   // the message was sent or deleted: the next one starts clean
            {
                current = null;
                currentText = null;
                Captures(false);
                popup.Forget();
                popup.ShowHint(anchor);
                return;
            }
            if (text == lastInserted)   // our own English, already in the box
            {
                if (current != null) { popup.ShowSuggestions(anchor, Rows(current), selected); Captures(true); }
                else { Captures(false); if (justSwitchedOn) popup.ShowHint(anchor); }
                return;
            }
            if (text.Length > 1500)
            {
                popup.ShowMessage(anchor, "লেখাটা অনেক বড়। বড় লেখার জন্য Ctrl+Shift+E দিয়ে অনুবাদকের উইন্ডো খুলুন।", false);
                return;
            }
            if (current != null && text == currentText)
            {
                popup.ShowSuggestions(anchor, Rows(current), selected);
                Captures(true);
                return;
            }
            if (busy && text == inflightText) return;

            Cancel();
            var my = new CancellationTokenSource();
            cts = my;
            busy = true;
            inflightText = text;
            popup.ShowLoading(anchor, settings.IsAccount);
            Captures(true);   // Alt+1 while waiting is kept and done when the answer arrives
            var ui = SynchronizationContext.Current;
            Action<Suggestions> progress = delegate(Suggestions part)
            {
                // Each version is shown as soon as it is complete; Alt+N still waits for the full answer.
                ui.Post(delegate { if (my == cts && busy && Active) popup.ShowSuggestions(anchor, Rows(part), -1); }, null);
            };
            try
            {
                Suggestions r = await Engine.Run(settings, text, "", false, my.Token, progress);
                if (my != cts) return;
                busy = false;
                inflightText = null;
                current = r;
                currentText = text;
                selected = -1;
                popup.ShowSuggestions(anchor, Rows(r), selected);
                Captures(true);
                if (pending >= 0) { int p = pending; pending = -1; Accept(p); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (my != cts) return;
                busy = false;
                inflightText = null;
                pending = -1;
                Captures(false);
                if (!(ex is EngineException)) Log.Write("inline fetch failed: " + ex.GetType().Name + ": " + ex.Message);
                popup.ShowMessage(anchor, ex is EngineException ? ex.Message : "সমস্যা হয়েছে: " + ex.Message, true);
            }
        }

        void Accept(int i)
        {
            if (!Active || i < 0 || i > 3) return;
            if (busy) { pending = i; return; }
            if (current == null) return;
            string[] rows = Rows(current);
            if (i >= rows.Length) return;
            string t = rows[i].Trim();
            if (t.Length == 0) return;
            lastInserted = t;
            current = null;
            currentText = null;
            selected = -1;
            Captures(false);
            Inserter.ReplaceFocused(t);
            popup.ShowDone(anchor);
        }

        // Ctrl+Enter: sends the version on show; with none chosen yet, the first one. The user's
        // own key press sends it: LazyReply never sends anything by itself.
        void SendChosen()
        {
            if (!Active || busy || current == null) return;
            string[] rows = Rows(current);
            bool inBox = selected >= 0;
            int i = inBox ? selected : 0;
            string t = rows[i].Trim();
            if (t.Length == 0) return;
            lastInserted = t;
            current = null;
            currentText = null;
            selected = -1;
            Captures(false);
            popup.Forget();
            popup.ShowMessage(anchor, "পাঠানো হয়েছে।", false);
            if (inBox) Inserter.PressEnter();
            else Inserter.ReplaceFocused(t, Inserter.PressEnter);
        }

        // Ctrl+Down / Ctrl+Up: the next or previous version replaces the box text, and the box keeps
        // the list open so the user can go on choosing.
        void Step(int dir)
        {
            if (!Active || busy || current == null) return;
            string[] rows = Rows(current);
            int n = rows.Length;
            selected = selected < 0 ? (dir > 0 ? 0 : n - 1) : (selected + dir + n) % n;
            string t = rows[selected].Trim();
            if (t.Length == 0) return;
            lastInserted = t;
            Inserter.ReplaceFocused(t);
            popup.ShowSuggestions(anchor, rows, selected);
        }
    }
}
