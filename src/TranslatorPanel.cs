using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace LazyReply
{
    // One suggestion: a title row ("1 · Professional", "Enter · Ctrl+1") and the text.
    class Card : Control
    {
        public string Title = "", Hint = "", Body = "";
        public bool Selected, Stale;
        // The panel's gliding highlight, in this card's coordinates (empty: not here). Each card
        // draws the part of it that passes over the card.
        public RectangleF Highlight;
        public float HighlightAlpha, OutlineAlpha;
        bool hover;
        readonly float sc;
        readonly Font fTitle, fBody;
        const TextFormatFlags BodyFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

        public Card(float sc, Font title, Font body)
        {
            this.sc = sc;
            fTitle = title;
            fBody = body;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        int S(int v) { return (int)Math.Round(v * sc); }
        string BodyText { get { return string.IsNullOrEmpty(Body) ? "…" : Body; } }

        public int HeightFor(int width)
        {
            int th = TextRenderer.MeasureText("Ag", fTitle).Height;
            Size b = TextRenderer.MeasureText(BodyText, fBody, new Size(width - 2 * S(12), int.MaxValue), BodyFlags);
            return S(9) + th + S(3) + b.Height + S(10);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            var r = new Rectangle(1, 1, Width - 3, Height - 3);
            using (var path = Theme.Round(r, S(12)))
            using (var b = new SolidBrush(hover ? Theme.Hover : Theme.Card))   // soft cells, no hard outlines
                g.FillPath(b, path);

            // How much of this card the highlight covers (0..1): the title colour follows it.
            float k = 0;
            if (!Highlight.IsEmpty)
            {
                float top = Math.Max(Highlight.Top, r.Top), bottom = Math.Min(Highlight.Bottom, r.Bottom);
                k = bottom <= top ? 0 : Math.Min(1f, (bottom - top) / r.Height) * HighlightAlpha;
            }
            if (k > 0)
            {
                int a = (int)(255 * HighlightAlpha);
                using (var clip = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), S(12) + 1))
                using (var hp = Theme.Round(Highlight, S(12)))
                using (var b = new SolidBrush(Color.FromArgb(a, Theme.AccentSoft)))
                using (var pen = new Pen(Color.FromArgb((int)(a * OutlineAlpha), Theme.Accent), 1.5f))
                {
                    g.SetClip(clip);   // the card's own rounded corners
                    g.FillPath(b, hp);
                    g.DrawPath(pen, hp);
                    g.ResetClip();
                }
            }
            Color titleColor = Theme.Mix(Theme.Muted, Theme.Accent, k);
            int th = TextRenderer.MeasureText("Ag", fTitle).Height;
            TextRenderer.DrawText(g, Title, fTitle, new Point(S(12), S(9)), titleColor, TextFormatFlags.NoPrefix);
            Size hs = TextRenderer.MeasureText(Hint, fTitle);
            TextRenderer.DrawText(g, Hint, fTitle, new Point(Width - S(12) - hs.Width, S(9)), titleColor, TextFormatFlags.NoPrefix);
            var body = new Rectangle(S(12), S(9) + th + S(3), Width - 2 * S(12), Height);
            TextRenderer.DrawText(g, BodyText, fBody, body, (Stale || string.IsNullOrEmpty(Body)) ? Theme.Muted : Theme.Text, BodyFlags);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    }

    // A panel that draws a rounded border around a text box.
    class Frame : Panel
    {
        public bool Active;
        readonly int radius;

        public Frame(int radius)
        {
            this.radius = radius;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.InputBg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            using (var path = Theme.Round(new Rectangle(1, 1, Width - 3, Height - 3), radius))
            {
                using (var b = new SolidBrush(Theme.InputBg)) g.FillPath(b, path);
                using (var pen = new Pen(Active ? Theme.Accent : Theme.Border, Active ? 1.5f : 1)) g.DrawPath(pen, path);
            }
        }
    }

    // The translator window. The user types Bangla (with Avro) or Banglish; after a short pause
    // three English versions appear. Enter puts the chosen one into the chat box behind.
    class TranslatorPanel : Form
    {
        public event Action SettingsRequested;

        readonly Settings settings;
        readonly float sc;
        readonly Font fTitle, fSmall, fInput, fBody, fCardTitle;
        readonly Panel header;
        readonly Label lblTitle, lblMode, lblCtx, lblSettings, lblClose, status, footer, inputPh, ctxPh;
        readonly Frame inputFrame, ctxFrame;
        readonly TextBox input, ctxBox;
        readonly Card[] cards = new Card[3];
        readonly System.Windows.Forms.Timer debounce;
        readonly Glider glider = new Glider();   // the highlight of the selected card (Ctrl+Up/Down)
        int glideCard = -1;

        CancellationTokenSource cts;
        Suggestions current;
        string currentKey, inflightKey;
        int pendingInsert = -1, selected;
        bool reverse, ctxVisible, busy, statusError, anchorBottom;
        int anchorY;
        IntPtr target;

        public TranslatorPanel(Settings settings)
        {
            this.settings = settings;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = Theme.Bg;
            Text = "LazyReply";
            using (var g = CreateGraphics()) sc = g.DpiX / 96f;

            fTitle = Theme.FB(10f);
            fSmall = Theme.F(8.75f);
            fInput = Theme.F(12.5f);
            fBody = Theme.F(11f);
            fCardTitle = Theme.F(8.75f);

            header = new Panel { BackColor = Theme.Bg };
            header.MouseDown += StartDrag;
            lblTitle = new Label { Text = "LazyReply", Font = fTitle, ForeColor = Theme.Text, AutoSize = true, BackColor = Theme.Bg };
            lblTitle.MouseDown += StartDrag;
            lblMode = Link("", fSmall, delegate { ToggleReverse(); });
            lblMode.BackColor = Theme.Card;
            lblMode.ForeColor = Theme.Muted;
            lblMode.Padding = new Padding(S(8), S(3), S(8), S(3));
            lblCtx = Link("প্রসঙ্গ", fSmall, delegate { ToggleContext(); });
            lblSettings = Link("সেটিংস", fSmall, delegate { if (SettingsRequested != null) SettingsRequested(); });
            lblClose = Link("×", Theme.F(14f), delegate { HidePanel(true); });
            header.Controls.AddRange(new Control[] { lblTitle, lblMode, lblCtx, lblSettings, lblClose });

            ctxFrame = new Frame(S(12)) { Visible = false };
            ctxBox = new TextBox
            {
                Multiline = true, BorderStyle = BorderStyle.None, Font = fSmall, ScrollBars = ScrollBars.Vertical,
                BackColor = Theme.InputBg, ForeColor = Theme.Text, AcceptsReturn = false
            };
            ctxPh = Placeholder("ক্লায়েন্টের শেষ মেসেজ এখানে পেস্ট করুন (ঐচ্ছিক)। উত্তর সেই কথার সাথে মিলিয়ে বানানো হবে।", fSmall, ctxBox);
            ctxFrame.Controls.Add(ctxBox);
            ctxFrame.Controls.Add(ctxPh);
            ctxPh.BringToFront();
            ctxBox.TextChanged += delegate { ctxPh.Visible = ctxBox.TextLength == 0; OnTextChangedAny(); };
            ctxBox.Enter += delegate { ctxFrame.Active = true; ctxFrame.Invalidate(); };
            ctxBox.Leave += delegate { ctxFrame.Active = false; ctxFrame.Invalidate(); };

            inputFrame = new Frame(S(14));
            input = new TextBox
            {
                Multiline = true, WordWrap = true, BorderStyle = BorderStyle.None, Font = fInput,
                BackColor = Theme.InputBg, ForeColor = Theme.Text, AcceptsReturn = true
            };
            inputPh = Placeholder("এখানে লিখুন… বাংলা (অভ্র) বা Banglish, দুটোই চলবে", fInput, input);
            inputFrame.Controls.Add(input);
            inputFrame.Controls.Add(inputPh);
            inputPh.BringToFront();
            input.TextChanged += delegate { inputPh.Visible = input.TextLength == 0; OnTextChangedAny(); };
            input.Enter += delegate { inputFrame.Active = true; inputFrame.Invalidate(); };
            input.Leave += delegate { inputFrame.Active = false; inputFrame.Invalidate(); };
            inputFrame.Click += delegate { input.Focus(); };

            status = new Label { Font = fSmall, ForeColor = Theme.Muted, BackColor = Theme.Bg, AutoSize = false };
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                cards[i] = new Card(sc, fCardTitle, fBody) { Visible = false };
                cards[i].Click += delegate { SelectCard(idx); RequestInsert(idx); };
            }
            footer = new Label { Font = fSmall, ForeColor = Theme.Muted, BackColor = Theme.Bg, AutoSize = false };

            Controls.Add(header);
            Controls.Add(ctxFrame);
            Controls.Add(inputFrame);
            Controls.Add(status);
            foreach (var c in cards) Controls.Add(c);
            Controls.Add(footer);

            debounce = new System.Windows.Forms.Timer { Interval = 650 };
            debounce.Tick += delegate { debounce.Stop(); Fetch(); };
            glider.Moved += PushHighlight;

            UpdateTexts();
            Relayout();
        }

        int S(int v) { return (int)Math.Round(v * sc); }

        Label Link(string text, Font font, EventHandler click)
        {
            var l = new Label { Text = text, Font = font, ForeColor = Theme.Muted, BackColor = Theme.Bg, AutoSize = true, Cursor = Cursors.Hand };
            l.Click += click;
            l.MouseEnter += delegate { if (l != lblMode) l.ForeColor = Theme.Text; };
            l.MouseLeave += delegate { if (l != lblMode) l.ForeColor = (l == lblCtx && ctxVisible) ? Theme.Accent : Theme.Muted; };
            return l;
        }

        Label Placeholder(string text, Font font, TextBox box)
        {
            var l = new Label { Text = text, Font = font, ForeColor = Theme.Muted, BackColor = Theme.InputBg, AutoSize = false, Cursor = Cursors.IBeam };
            l.Click += delegate { box.Focus(); };
            return l;
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; }   // CS_DROPSHADOW
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Soft.Window(Handle, false);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Theme.Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        void StartDrag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            Native.ReleaseCapture();
            Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, new IntPtr(Native.HTCAPTION), IntPtr.Zero);
            anchorBottom = true;     // the move loop has ended: keep the bottom edge where the user put it
            anchorY = Bottom;
        }

        // ---------- open / close ----------

        public void OpenFor(IntPtr chatWindow)
        {
            if (chatWindow != IntPtr.Zero) target = chatWindow;
            if (!Visible) PlaceNear(target);
            Show();
            FollowSelection();
            Native.ForceForeground(Handle);
            Activate();
            input.Focus();
            input.SelectionStart = input.TextLength;
        }

        public void HidePanel(bool returnFocus)
        {
            debounce.Stop();
            pendingInsert = -1;
            glider.Clear();   // the next opening fades the highlight in again
            glideCard = -1;
            Hide();
            if (returnFocus && target != IntPtr.Zero && Native.IsWindow(target)) Native.ForceForeground(target);
        }

        public bool IsActiveWindow { get { return Visible && Native.GetForegroundWindow() == Handle; } }

        // Above the caret of the chat box (chat boxes are usually low on the screen);
        // without a known caret, near the bottom of the chat window.
        void PlaceNear(IntPtr hwnd)
        {
            Rectangle caret = Native.CaretRect(hwnd);
            Rectangle win = Native.WindowRect(hwnd);
            Point probe = caret != Rectangle.Empty ? caret.Location : (win != Rectangle.Empty ? new Point(win.Left + win.Width / 2, win.Bottom - 10) : Cursor.Position);
            Rectangle wa = Screen.FromPoint(probe).WorkingArea;
            int expected = S(380);
            int x;
            if (caret != Rectangle.Empty)
            {
                x = caret.Left - S(40);
                if (caret.Top - S(12) - expected >= wa.Top) { anchorBottom = true; anchorY = caret.Top - S(12); }
                else { anchorBottom = false; Top = caret.Bottom + S(12); }
            }
            else if (win != Rectangle.Empty)
            {
                x = win.Left + (win.Width - Width) / 2;
                anchorBottom = true;
                anchorY = Math.Min(win.Bottom, wa.Bottom) - S(110);
            }
            else
            {
                x = wa.Left + (wa.Width - Width) / 2;
                anchorBottom = true;
                anchorY = wa.Bottom - S(60);
            }
            Left = Math.Max(wa.Left + 8, Math.Min(x, wa.Right - Width - 8));
            Relayout();
        }

        // ---------- keys ----------

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape: HidePanel(true); return true;
                case Keys.Enter:
                    if (ActiveControl == ctxBox) { input.Focus(); return true; }
                    RequestInsert(selected);
                    return true;
                case Keys.Control | Keys.D1: case Keys.Control | Keys.NumPad1: SelectCard(0); RequestInsert(0); return true;
                case Keys.Control | Keys.D2: case Keys.Control | Keys.NumPad2: SelectCard(1); RequestInsert(1); return true;
                case Keys.Control | Keys.D3: case Keys.Control | Keys.NumPad3: SelectCard(2); RequestInsert(2); return true;
                case Keys.Control | Keys.Up: SelectCard(selected - 1); return true;
                case Keys.Control | Keys.Down: SelectCard(selected + 1); return true;
                case Keys.Control | Keys.R: ToggleReverse(); return true;
                case Keys.Control | Keys.K: ToggleContext(); return true;
                case Keys.Control | Keys.Shift | Keys.C: CopySelected(); return true;
                case Keys.Control | Keys.A:
                    var tb = ActiveControl as TextBox;
                    if (tb != null) { tb.SelectAll(); return true; }
                    break;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void SelectCard(int i)
        {
            int n = reverse ? 1 : 3;
            selected = Math.Max(0, Math.Min(n - 1, i));
            UpdateTexts();
            FollowSelection();
        }

        // The highlight follows the selected card: it glides when the selection moves to another card.
        void FollowSelection()
        {
            if (!Visible) return;   // the cards of a hidden window all read as hidden: OpenFor follows once shown
            int sel = -1;
            for (int i = 0; i < cards.Length; i++) if (cards[i].Visible && cards[i].Selected) sel = i;
            if (sel < 0) { glider.Clear(); glideCard = -1; PushHighlight(); return; }
            var c = cards[sel];
            var r = new RectangleF(c.Left + 1, c.Top + 1, c.Width - 3, c.Height - 3);
            if (sel == glideCard) glider.Retarget(r);
            else glider.GoTo(r, glideCard >= 0);
            glideCard = sel;
            PushHighlight();
        }

        void PushHighlight()
        {
            foreach (var c in cards)
            {
                RectangleF h = glider.Now;
                if (!h.IsEmpty) h.Offset(-c.Left, -c.Top);
                c.Highlight = h;
                c.HighlightAlpha = glider.Alpha;
                c.OutlineAlpha = glider.OutlineAlpha;
                c.Invalidate();
            }
        }

        void ToggleReverse()
        {
            reverse = !reverse;
            selected = 0;
            current = null;
            currentKey = null;
            CancelFetch();
            UpdateTexts();
            if (input.Text.Trim().Length > 0) Fetch();
            Relayout();
            input.Focus();
        }

        void ToggleContext()
        {
            ctxVisible = !ctxVisible;
            lblCtx.ForeColor = ctxVisible ? Theme.Accent : Theme.Muted;
            ctxFrame.Visible = ctxVisible;
            Relayout();
            if (ctxVisible) ctxBox.Focus(); else input.Focus();
            OnTextChangedAny();
        }

        // ---------- suggestions ----------

        string MakeKey()
        {
            return (reverse ? "R|" : "F|") + input.Text.Trim() + "|" + (ctxVisible && !reverse ? ctxBox.Text.Trim() : "");
        }

        void OnTextChangedAny()
        {
            debounce.Stop();
            if (input.Text.Trim().Length == 0)
            {
                CancelFetch();
                current = null;
                currentKey = null;
                pendingInsert = -1;
                SetStatus("", false);
            }
            else if (MakeKey() != currentKey)
            {
                debounce.Start();
                foreach (var c in cards) { c.Stale = true; c.Invalidate(); }
            }
            UpdateTexts();
            Relayout();
        }

        void CancelFetch()
        {
            if (cts != null) cts.Cancel();
            cts = null;
            busy = false;
            inflightKey = null;
        }

        async void Fetch()
        {
            debounce.Stop();
            string text = input.Text.Trim();
            if (text.Length == 0) return;
            string key = MakeKey();
            if (current != null && key == currentKey)
            {
                if (pendingInsert >= 0) { int p = pendingInsert; pendingInsert = -1; DoInsert(p); }
                return;
            }
            if (busy && key == inflightKey) return;
            CancelFetch();
            var my = new CancellationTokenSource();
            cts = my;
            busy = true;
            inflightKey = key;
            bool rev = reverse;
            SetStatus(rev ? "বাংলা মানে বের করছি…" : "ইংরেজি বানাচ্ছি…", false);
            UpdateTexts();
            Relayout();
            try
            {
                Suggestions r = await Engine.Run(settings, text, ctxVisible ? ctxBox.Text.Trim() : "", rev, my.Token);
                if (my != cts) return;
                busy = false;
                inflightKey = null;
                current = r;
                currentKey = key;
                SetStatus("", false);
                UpdateTexts();
                Relayout();
                if (pendingInsert >= 0) { int p = pendingInsert; pendingInsert = -1; DoInsert(p); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (my != cts) return;
                busy = false;
                inflightKey = null;
                pendingInsert = -1;
                if (!(ex is EngineException)) Log.Write("fetch failed: " + ex.GetType().Name + ": " + ex.Message);
                SetStatus(ex is EngineException ? ex.Message : "সমস্যা হয়েছে: " + ex.Message, true);
                UpdateTexts();
                Relayout();
            }
        }

        void RequestInsert(int i)
        {
            if (input.Text.Trim().Length == 0) return;
            if (reverse) i = 0;
            if (current != null && currentKey == MakeKey()) { DoInsert(i); return; }
            pendingInsert = i;
            SetStatus("তৈরি হলেই বসিয়ে দেব…", false);
            Relayout();
            Fetch();
        }

        string TextOf(int i)
        {
            if (current == null) return "";
            if (reverse) return current.Bangla;
            return i == 0 ? current.Professional : i == 1 ? current.Friendly : current.Short;
        }

        void DoInsert(int i)
        {
            string t = TextOf(i).Trim();
            if (t.Length == 0) return;
            if (reverse)
            {
                Inserter.CopyOnly(t);
                SetStatus("বাংলা মানে কপি হয়েছে।", false);
                Relayout();
                return;
            }
            if (target == IntPtr.Zero || !Native.IsWindow(target))
            {
                Inserter.CopyOnly(t);
                SetStatus("কোন চ্যাটে বসাব, তা পাওয়া যায়নি। লেখাটি কপি হয়েছে: চ্যাটে গিয়ে Ctrl+V দিন।", true);
                Relayout();
                return;
            }
            Inserter.PasteInto(target, t);   // brings the chat window forward first, then pastes
            HidePanel(false);
            input.Text = "";
            ctxBox.Text = "";
            selected = 0;
        }

        void CopySelected()
        {
            string t = TextOf(selected).Trim();
            if (t.Length == 0) return;
            Inserter.CopyOnly(t);
            SetStatus("কপি হয়েছে।", false);
            Relayout();
        }

        void SetStatus(string text, bool error)
        {
            status.Text = text;
            statusError = error;
            status.ForeColor = error ? Theme.Danger : Theme.Muted;
        }

        // ---------- texts and layout ----------

        string Hotkey { get { var k = KeyCombo.Parse(settings.PanelHotkey); return k == null ? "" : k.ToString(); } }

        void UpdateTexts()
        {
            lblMode.Text = reverse ? "English → বাংলা" : "বাংলা → English";
            bool hasText = input.Text.Trim().Length > 0;
            string[] titles = reverse
                ? new[] { "বাংলা মানে", "কী চাইছে / টোন", "" }
                : new[] { "1 · Professional", "2 · Friendly", "3 · Short" };
            for (int i = 0; i < 3; i++)
            {
                var c = cards[i];
                bool show = hasText && (current != null || busy || pendingInsert >= 0) && (!reverse || i < 2);
                c.Visible = show;
                c.Title = titles[i];
                c.Selected = i == selected && !(reverse && i == 1);
                if (reverse) c.Hint = i == 0 ? "Enter: কপি" : "";
                else c.Hint = (i == selected ? "Enter · " : "") + "Ctrl+" + (i + 1);
                c.Body = current == null ? "" : (reverse ? (i == 0 ? current.Bangla : current.Note) : TextOf(i));
                if (reverse && i == 1 && current != null && current.Note.Length == 0) c.Visible = false;
                c.Stale = current != null && MakeKey() != currentKey;
                c.Invalidate();
            }
            if (!hasText && status.Text.Length == 0)
                status.Text = reverse
                    ? "ক্লায়েন্টের ইংরেজি মেসেজ পেস্ট করুন। থামলেই বাংলা মানে আসবে।"
                    : "লেখা থামালেই তিন রকম ইংরেজি বাক্য আসবে।";
            else if (hasText && !busy && !statusError && status.Text.StartsWith(reverse ? "ক্লায়েন্টের ইংরেজি" : "লেখা থামালেই"))
                status.Text = "";
            footer.Text = reverse
                ? "Enter: কপি · Ctrl+R: বাংলা → English · Esc: বন্ধ"
                : "Enter: চ্যাটে বসাও · Ctrl+1/2/3: সরাসরি · Ctrl+↑/↓: বাছাই · Ctrl+Shift+C: কপি · Ctrl+R: উল্টো অনুবাদ · Ctrl+K: প্রসঙ্গ · Esc: বন্ধ"
                  + (Hotkey.Length > 0 ? " · খুলতে: " + Hotkey : "");
        }

        void Relayout()
        {
            int W = S(600), m = S(16), inner = W - 2 * m;
            SuspendLayout();

            header.Bounds = new Rectangle(0, 0, W, S(46));
            int cy = header.Height / 2;
            lblTitle.Location = new Point(m, cy - lblTitle.PreferredHeight / 2);
            lblMode.Location = new Point(lblTitle.Right + S(10), cy - lblMode.PreferredHeight / 2);
            int rx = W - S(10);
            lblClose.Location = new Point(rx - lblClose.PreferredWidth, cy - lblClose.PreferredHeight / 2 - S(1)); rx = lblClose.Left - S(10);
            lblSettings.Location = new Point(rx - lblSettings.PreferredWidth, cy - lblSettings.PreferredHeight / 2); rx = lblSettings.Left - S(14);
            lblCtx.Location = new Point(rx - lblCtx.PreferredWidth, cy - lblCtx.PreferredHeight / 2);
            int y = header.Bottom;

            if (ctxVisible && !reverse)
            {
                ctxFrame.Visible = true;
                ctxFrame.Bounds = new Rectangle(m, y, inner, S(74));
                ctxBox.Bounds = new Rectangle(S(10), S(8), inner - S(20), S(74) - S(16));
                ctxPh.Bounds = ctxBox.Bounds;
                y = ctxFrame.Bottom + S(8);
            }
            else ctxFrame.Visible = false;

            int lineH = TextRenderer.MeasureText("আমি Ag", fInput).Height;
            int textW = inner - S(24);
            int textH = TextRenderer.MeasureText(input.Text.Length == 0 ? "A" : input.Text + "\nA", fInput, new Size(textW, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height - lineH;
            textH = Math.Max(lineH, Math.Min(textH, lineH * 5));
            input.ScrollBars = textH >= lineH * 5 ? ScrollBars.Vertical : ScrollBars.None;
            inputFrame.Bounds = new Rectangle(m, y, inner, textH + S(22));
            input.Bounds = new Rectangle(S(12), S(11), textW, textH);
            inputPh.Bounds = new Rectangle(S(12), S(11), textW, lineH);
            y = inputFrame.Bottom + S(8);

            if (status.Text.Length > 0)
            {
                int sh = TextRenderer.MeasureText(status.Text, fSmall, new Size(inner, int.MaxValue), TextFormatFlags.WordBreak).Height;
                status.Visible = true;
                status.Bounds = new Rectangle(m, y, inner, sh);
                y = status.Bottom + S(8);
            }
            else status.Visible = false;

            foreach (var c in cards)
            {
                if (!c.Visible) continue;
                int h = c.HeightFor(inner);
                c.Bounds = new Rectangle(m, y, inner, h);
                y = c.Bottom + S(8);
            }

            int fh = TextRenderer.MeasureText(footer.Text, fSmall, new Size(inner, int.MaxValue), TextFormatFlags.WordBreak).Height;
            footer.Bounds = new Rectangle(m, y + S(2), inner, fh);
            y = footer.Bottom + S(14);

            ClientSize = new Size(W, y);
            if (anchorBottom)
            {
                Rectangle wa = Screen.FromPoint(new Point(Left + Width / 2, anchorY - 10)).WorkingArea;
                Top = Math.Max(wa.Top + 8, anchorY - Height);
            }
            ResumeLayout();
            FollowSelection();
            Invalidate(true);   // children too: moved labels must not keep old pixels
        }
    }
}
