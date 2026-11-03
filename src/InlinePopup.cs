using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LazyReply
{
    // The small box that floats just above the chat box while inline translation is on.
    // Like the tip, it never takes the keyboard focus: the user keeps typing in the chat box.
    class InlinePopup : Form
    {
        public event Action<int> RowClicked;
        public event Action CloseClicked;

        enum Mode { Hint, Loading, Suggestions, Message, Done }

        static readonly string[] Kinds = { "Professional", "Friendly", "Short", "বাংলা অনুবাদ" };
        const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

        readonly float sc;
        readonly Font fSmall, fBody, fBadge, fClose, fName;
        readonly Timer autoHide;
        Mode mode;
        string message = "";
        bool messageError, stale, slow;
        string[] rows = new string[0];
        Rectangle rClose, rMessage, lastAnchor;
        Rectangle[] rRows = new Rectangle[0], rTexts = new Rectangle[0];
        int hover = -1, headerH, chosen = -1;
        readonly Glider glider = new Glider();   // the highlight of the chosen version (Ctrl+Up/Down)

        public InlinePopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            BackColor = Theme.Bg;
            Text = "LazyReply inline";
            using (var g = CreateGraphics()) sc = g.DpiX / 96f;
            fSmall = Theme.F(8.5f);
            fBody = Theme.F(10.5f);
            fBadge = Theme.FB(8f);
            fClose = Theme.F(12f);
            fName = Theme.FB(9f);
            autoHide = new Timer();
            // "Done" and error notes last a few seconds, then the box goes back to its hint: it stays
            // open until the user clicks "×".
            autoHide.Tick += delegate { autoHide.Stop(); if (Visible) ShowHint(lastAnchor); };
            glider.Moved += delegate { Invalidate(); };
        }

        int S(int v) { return (int)Math.Round(v * sc); }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW: the card floats a little above the chat
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Soft.Window(Handle, false);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = new IntPtr(Native.MA_NOACTIVATE); return; }
            base.WndProc(ref m);
        }

        // ---------- states ----------

        public bool HasSuggestions { get { return Visible && mode == Mode.Suggestions && !stale; } }

        public void ShowHint(Rectangle anchor) { mode = Mode.Hint; Place(anchor, 0); }

        // Forgets the last versions, so a new message never shows another message's suggestions.
        public void Forget()
        {
            rows = new string[0];
            chosen = -1;
            stale = false;
        }

        public void ShowLoading(Rectangle anchor, bool slowProvider)
        {
            mode = Mode.Loading;
            slow = slowProvider;
            stale = rows.Length > 0;
            Place(anchor, 0);
        }

        public void ShowSuggestions(Rectangle anchor, string[] suggestions, int selectedRow)
        {
            // The highlight moves on from where it is only within the same list on show.
            int was = Visible && mode == Mode.Suggestions && !stale && rows.Length == suggestions.Length ? chosen : -1;
            rows = suggestions;
            chosen = selectedRow;
            stale = false;
            mode = Mode.Suggestions;
            Place(anchor, 0);
            Glide(was);
        }

        void Glide(int was)
        {
            if (chosen < 0 || chosen >= rRows.Length) { glider.Clear(); return; }
            glider.GoTo(rRows[chosen], was >= 0);
        }

        public void ShowMessage(Rectangle anchor, string text, bool error)
        {
            mode = Mode.Message;
            message = text;
            messageError = error;
            Place(anchor, 9000);
        }

        public void ShowDone(Rectangle anchor)
        {
            mode = Mode.Done;
            rows = new string[0];
            Place(anchor, 2200);
        }

        public void MarkStale()
        {
            if (Visible && mode == Mode.Suggestions && !stale) { stale = true; Invalidate(); }
        }

        public void HidePopup()
        {
            autoHide.Stop();
            glider.Clear();
            hover = -1;
            if (Visible) Hide();
        }

        // Just above the chat box; below it when there is no room above.
        void Place(Rectangle anchor, int hideAfterMs)
        {
            lastAnchor = anchor;
            Rectangle wa = Screen.FromPoint(new Point(anchor.Left + anchor.Width / 2, anchor.Top)).WorkingArea;
            int w = Math.Max(S(400), Math.Min(anchor.Width, S(640)));
            Relayout(w);
            int x = Math.Max(wa.Left + 4, Math.Min(anchor.Left, wa.Right - Width - 4));
            int y = anchor.Top - Height - S(8);
            if (y < wa.Top + 4) y = Math.Min(anchor.Bottom + S(8), wa.Bottom - Height - 4);
            Location = new Point(x, y);
            if (!Visible) Show();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            autoHide.Stop();
            if (hideAfterMs > 0) { autoHide.Interval = hideAfterMs; autoHide.Start(); }
            Invalidate();
        }

        string Status
        {
            get
            {
                switch (mode)
                {
                    case Mode.Hint: return "চ্যাটের বক্সে বাংলা বা Banglish লিখুন। দাঁড়ি (।) বা ? দিলে এখানে ইংরেজি আসবে।";
                    case Mode.Loading: return "ইংরেজি বানাচ্ছি…";
                    case Mode.Done: return "বসানো হয়েছে। দেখে নিয়ে Enter চেপে পাঠান।";
                    case Mode.Message: return message;
                }
                return "";
            }
        }

        string HeaderHint
        {
            get { return mode == Mode.Suggestions || (mode == Mode.Loading && stale) ? "Ctrl ধরে ↑↓ · Ctrl+Enter পাঠাও" : ""; }
        }

        const string Pill = "বাংলা → English";
        Rectangle rPill;

        void Relayout(int w)
        {
            int pad = S(14);
            headerH = Math.Max(TextRenderer.MeasureText("Ag", fName).Height, S(24));
            rClose = new Rectangle(w - pad - S(26) + S(4), pad - S(2), S(26), S(26));
            Size ns = TextRenderer.MeasureText("LazyReply", fName);
            Size ps = TextRenderer.MeasureText(Pill, fSmall);
            rPill = new Rectangle(pad + S(14) + ns.Width + S(8), pad + (headerH - ps.Height - S(4)) / 2, ps.Width + S(16), ps.Height + S(4));
            int y = pad + headerH + S(10);
            bool showRows = mode == Mode.Suggestions || (mode == Mode.Loading && stale);

            rMessage = Rectangle.Empty;
            if (mode != Mode.Suggestions)
            {
                int h = TextRenderer.MeasureText(Status, fBody, new Size(w - 2 * pad - S(8), int.MaxValue), Wrap).Height;
                rMessage = new Rectangle(pad + S(4), y, w - 2 * pad - S(8), h);
                y += h + S(10);
            }

            if (showRows)
            {
                rRows = new Rectangle[rows.Length];
                rTexts = new Rectangle[rows.Length];
                int inner = S(12), badge = S(24);
                int textW = w - 2 * pad - 2 * inner - badge - S(10);
                int kindH = TextRenderer.MeasureText("Ag", fSmall).Height;
                for (int i = 0; i < rows.Length; i++)
                {
                    int h = TextRenderer.MeasureText(rows[i].Length == 0 ? "…" : rows[i], fBody, new Size(textW, int.MaxValue), Wrap).Height;
                    rRows[i] = new Rectangle(pad, y, w - 2 * pad, inner + kindH + S(2) + h + inner);
                    rTexts[i] = new Rectangle(pad + inner + badge + S(10), y + inner + kindH + S(2), textW, h);
                    y = rRows[i].Bottom + S(6);
                }
                y -= S(6);
                y += S(4);
            }
            else { rRows = new Rectangle[0]; rTexts = new Rectangle[0]; }

            Size = new Size(w, y + pad - S(4));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

            // Header: a live dot, the name, a soft pill, the keys, and a quiet close button.
            int pad = S(14), cy = pad + headerH / 2;
            using (var b = new SolidBrush(Theme.Bangla)) g.FillEllipse(b, pad, cy - S(4), S(8), S(8));
            Size ns = TextRenderer.MeasureText("LazyReply", fName);
            TextRenderer.DrawText(g, "LazyReply", fName, new Point(pad + S(14), cy - ns.Height / 2), Theme.Text, TextFormatFlags.NoPrefix);
            Soft.Fill(g, rPill, rPill.Height / 2, Theme.Card);
            TextRenderer.DrawText(g, Pill, fSmall, rPill, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            string hint = HeaderHint;
            if (hint.Length > 0)
            {
                Size hs = TextRenderer.MeasureText(hint, fSmall);
                int hx = rClose.Left - S(8) - hs.Width;
                if (hx > rPill.Right + S(10)) TextRenderer.DrawText(g, hint, fSmall, new Point(hx, cy - hs.Height / 2), Theme.Muted, TextFormatFlags.NoPrefix);
            }
            if (hover == 100) Soft.Fill(g, rClose, S(8), Theme.Hover);
            TextRenderer.DrawText(g, "×", fClose, rClose, hover == 100 ? Theme.Text : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            if (!rMessage.IsEmpty)
            {
                Color c = mode == Mode.Message && messageError ? Theme.Danger : (mode == Mode.Done ? Theme.Bangla : (mode == Mode.Hint || mode == Mode.Loading ? Theme.Muted : Theme.Text));
                TextRenderer.DrawText(g, Status, fBody, rMessage, c, Wrap);
            }

            // Versions: soft rounded cells; the one in the chat box is tinted with an accent outline,
            // which glides between rows.
            bool showHl = mode == Mode.Suggestions && !stale && chosen >= 0 && chosen < rRows.Length && !glider.Now.IsEmpty;
            int rad = S(12);
            for (int i = 0; i < rRows.Length; i++)
                if (hover == i && !stale && !(showHl && i == chosen)) Soft.Fill(g, rRows[i], rad, Theme.Hover);
            if (showHl)
            {
                int a = (int)(255 * glider.Alpha);
                using (var p = Theme.Round(glider.Now, rad))
                using (var b = new SolidBrush(Color.FromArgb(a, Theme.AccentSoft)))
                using (var pen = new Pen(Color.FromArgb((int)(a * glider.OutlineAlpha), Theme.Accent), 1.5f))
                {
                    g.FillPath(b, p);
                    g.DrawPath(pen, p);
                }
            }

            for (int i = 0; i < rRows.Length; i++)
            {
                float k = showHl ? glider.Cover(rRows[i]) : 0;   // the badge and label follow the highlight
                var badge = new Rectangle(rRows[i].Left + S(12), rRows[i].Top + S(12), S(24), S(24));
                Soft.Fill(g, badge, S(8), Theme.Mix(Theme.Card, Theme.Accent, k));
                if (k < 1) Soft.Outline(g, badge, S(8), Color.FromArgb((int)(255 * (1 - k)), Theme.Border), 1);
                TextRenderer.DrawText(g, (i + 1).ToString(), fBadge, badge, Theme.Mix(Theme.Muted, Theme.OnAccent, k),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

                string kind = (i < Kinds.Length ? Kinds[i] : "") + (k > 0.5f ? "  ·  বক্সে আছে" : "  ·  Alt+" + (i + 1));
                TextRenderer.DrawText(g, kind, fSmall, new Point(rTexts[i].Left, rRows[i].Top + S(12)), Theme.Mix(Theme.Muted, Theme.Accent, k), TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, rows[i].Length == 0 ? "…" : rows[i], fBody, rTexts[i], stale || rows[i].Length == 0 ? Theme.Muted : Theme.Text, Wrap);
            }
        }

        int HitTest(Point p)
        {
            if (rClose.Contains(p)) return 100;
            if (stale) return -1;
            for (int i = 0; i < rRows.Length; i++) if (rRows[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            int h = HitTest(e.Location);
            if (h == 100) { if (CloseClicked != null) CloseClicked(); }
            else if (h >= 0 && RowClicked != null) RowClicked(h);
        }
    }
}
