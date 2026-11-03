using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LazyReply
{
    // The small tip that appears when Avro switches to Bangla. It never takes the keyboard focus,
    // so the user can keep typing in the chat box while it is on screen.
    class PromptToast : Form
    {
        public event Action OpenClicked;
        public event Action DismissForever;

        readonly float sc;
        readonly Font fSmall, fTitle, fBtn, fClose;
        readonly Timer hideTimer;
        Rectangle rOpen, rLater, rClose;
        int hover = -1;
        string hotkey = "";

        public PromptToast()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            BackColor = Theme.Bg;
            Text = "LazyReply tip";
            using (var g = CreateGraphics()) sc = g.DpiX / 96f;
            fSmall = Theme.F(8.5f);
            fTitle = Theme.FB(10.5f);
            fBtn = Theme.F(9.5f);
            fClose = Theme.F(13f);
            int captionW = TextRenderer.MeasureText(Caption, fSmall).Width;
            Size = new Size(Math.Max(S(330), captionW + S(36)), S(138));
            rClose = new Rectangle(Width - S(38), S(10), S(26), S(26));
            hideTimer = new Timer { Interval = 8000 };
            hideTimer.Tick += delegate { HideToast(); };
        }

        int S(int v) { return (int)Math.Round(v * sc); }

        const string Caption = "× চাপলে এই টিপ আর আসবে না · সেটিংস থেকে ফেরানো যায়";

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
                cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW
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

        // Above the caret, so Avro's own word list (which opens under the caret) stays visible.
        public void ShowNear(Rectangle caret, string hotkeyText)
        {
            hotkey = hotkeyText;
            Rectangle wa = Screen.FromPoint(caret.Location).WorkingArea;
            int x = caret.Left - S(16);
            int y = caret.Top - Height - S(10);
            if (y < wa.Top + 4) y = caret.Bottom + S(40);
            x = Math.Max(wa.Left + 4, Math.Min(x, wa.Right - Width - 4));
            y = Math.Max(wa.Top + 4, Math.Min(y, wa.Bottom - Height - 4));
            Location = new Point(x, y);
            LayoutButtons();
            if (!Visible) Show();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            hideTimer.Stop();
            hideTimer.Start();
            Invalidate();
        }

        public void HideToast()
        {
            hideTimer.Stop();
            hover = -1;
            if (Visible) Hide();
        }

        void LayoutButtons()
        {
            string open = OpenLabel();
            int wOpen = TextRenderer.MeasureText(open, fBtn).Width + S(32);
            int wLater = TextRenderer.MeasureText("এবার না", fBtn).Width + S(32);
            rOpen = new Rectangle(S(18), S(68), wOpen, S(34));
            rLater = new Rectangle(rOpen.Right + S(8), S(68), wLater, S(34));
        }

        string OpenLabel() { return string.IsNullOrEmpty(hotkey) ? "চালু করুন" : "চালু করুন  " + hotkey; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);
            using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

            using (var b = new SolidBrush(Theme.Bangla)) g.FillEllipse(b, S(18), S(19), S(8), S(8));
            TextRenderer.DrawText(g, "Avro: বাংলা চালু হলো", fSmall, new Point(S(31), S(13)), Theme.Muted, TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "ইংরেজি অনুবাদ চালু করবেন?", fTitle, new Point(S(17), S(33)), Theme.Text, TextFormatFlags.NoPrefix);

            Soft.Fill(g, rOpen, rOpen.Height / 2, hover == 0 ? ControlPaint.Dark(Theme.Accent, 0.04f) : Theme.Accent);
            TextRenderer.DrawText(g, OpenLabel(), fBtn, rOpen, Theme.OnAccent,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            Soft.Fill(g, rLater, rLater.Height / 2, hover == 1 ? Theme.Hover : Theme.Bg);
            Soft.Outline(g, rLater, rLater.Height / 2, Theme.Border, 1);
            TextRenderer.DrawText(g, "এবার না", fBtn, rLater, Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            if (hover == 2) Soft.Fill(g, rClose, S(8), Theme.Hover);
            TextRenderer.DrawText(g, "×", fClose, rClose, hover == 2 ? Theme.Text : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(g, Caption, fSmall, new Point(S(17), S(110)), Theme.Muted, TextFormatFlags.NoPrefix);
        }

        int HitTest(Point p)
        {
            if (rOpen.Contains(p)) return 0;
            if (rLater.Contains(p)) return 1;
            if (rClose.Contains(p)) return 2;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            Cursor = h >= 0 ? Cursors.Hand : Cursors.Default;
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { hideTimer.Stop(); }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = -1;
            Invalidate();
            if (Visible) hideTimer.Start();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            int h = HitTest(e.Location);
            if (h < 0) return;
            HideToast();
            if (h == 0 && OpenClicked != null) OpenClicked();
            if (h == 2 && DismissForever != null) DismissForever();
        }
    }
}
