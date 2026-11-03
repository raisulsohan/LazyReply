using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LazyReply
{
    // The soft look shared by every window: rounded cards, pill buttons, toggle switches.
    static class Soft
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        static int ColorRef(Color c) { return c.R | (c.G << 8) | (c.B << 16); }

        static void Dwm(IntPtr h, int attr, int value)
        {
            try { DwmSetWindowAttribute(h, attr, ref value, 4); } catch { }
        }

        // Windows 11: rounded corners, a soft hairline border, and a title bar in the page colour.
        public static void Window(IntPtr hwnd, bool titleBar)
        {
            Dwm(hwnd, 33, 2);                          // DWMWCP_ROUND
            Dwm(hwnd, 34, ColorRef(Theme.Border));     // border colour
            if (!titleBar) return;
            Dwm(hwnd, 20, Theme.Dark ? 1 : 0);         // dark title bar in dark mode
            Dwm(hwnd, 35, ColorRef(Theme.Page));       // caption colour
            Dwm(hwnd, 36, ColorRef(Theme.Text));       // caption text colour
        }

        public static void Fill(Graphics g, Rectangle r, int radius, Color fill)
        {
            using (var p = Theme.Round(r, radius)) using (var b = new SolidBrush(fill)) g.FillPath(b, p);
        }

        public static void Outline(Graphics g, Rectangle r, int radius, Color line, float width)
        {
            using (var p = Theme.Round(r, radius)) using (var pen = new Pen(line, width)) g.DrawPath(pen, p);
        }

        public static Color ParentBack(Control c)
        {
            return c.Parent != null ? c.Parent.BackColor : Theme.Page;
        }

        public static Label Text(string text, Font font, Color color, Color back)
        {
            return new Label { Text = text, Font = font, ForeColor = color, BackColor = back, AutoSize = true, UseMnemonic = false };
        }

        public static ToolStripRenderer MenuRenderer() { return new SoftMenuRenderer(); }

        class SoftMenuRenderer : ToolStripProfessionalRenderer
        {
            public SoftMenuRenderer() : base(new SoftMenuColors()) { RoundedEdges = true; }
            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = Theme.Text;
                base.OnRenderItemText(e);
            }
        }

        class SoftMenuColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Bg; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Bg; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Bg; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Bg; } }
            public override Color MenuBorder { get { return Theme.Border; } }
            public override Color MenuItemBorder { get { return Theme.AccentSoft; } }
            public override Color MenuItemSelected { get { return Theme.AccentSoft; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.AccentSoft; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.AccentSoft; } }
            public override Color SeparatorDark { get { return Theme.Border; } }
            public override Color SeparatorLight { get { return Theme.Bg; } }
            public override Color CheckBackground { get { return Theme.AccentSoft; } }
            public override Color CheckSelectedBackground { get { return Theme.AccentSoft; } }
        }
    }

    // The accent highlight of a chosen version: it glides to its new place with an ease-out instead
    // of jumping, and fades in where it first appears. Between two places its outline fades out and
    // back, so the line never strikes through the text it passes. Now is empty while there is none.
    class Glider
    {
        const int Ms = 160;
        public event Action Moved;

        readonly Timer timer = new Timer { Interval = 15 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        RectangleF from, to;
        float alphaFrom;
        bool moving;
        long start;

        public RectangleF Now { get; private set; }
        public float Alpha { get; private set; }
        public float OutlineAlpha { get; private set; }

        public Glider() { timer.Tick += delegate { Step(); }; }

        // moveOn: on from where it is now (even mid-glide); otherwise it fades in at r.
        public void GoTo(RectangleF r, bool moveOn)
        {
            if (moveOn && r == to && !Now.IsEmpty) return;
            to = r;
            if (!Native.AnimationsOn) { timer.Stop(); Now = r; Alpha = 1; OutlineAlpha = 1; if (Moved != null) Moved(); return; }
            moving = moveOn && !Now.IsEmpty;
            if (moving) { from = Now; alphaFrom = Alpha; }
            else { from = RectangleF.Inflate(r, -r.Width * 0.02f, -r.Height * 0.08f); alphaFrom = 0; }
            start = clock.ElapsedMilliseconds;
            timer.Start();
            Step();
        }

        // The same place has moved (a new layout): follow it without a new glide.
        public void Retarget(RectangleF r)
        {
            to = r;
            if (!timer.Enabled && !Now.IsEmpty) Now = r;
        }

        public void Clear()
        {
            timer.Stop();
            Now = RectangleF.Empty;
        }

        void Step()
        {
            float t = Math.Min(1f, (clock.ElapsedMilliseconds - start) / (float)Ms);
            float e = 1 - (1 - t) * (1 - t) * (1 - t);
            Now = new RectangleF(Lerp(from.X, to.X, e), Lerp(from.Y, to.Y, e), Lerp(from.Width, to.Width, e), Lerp(from.Height, to.Height, e));
            Alpha = Lerp(alphaFrom, 1, e);
            OutlineAlpha = moving ? (2 * e - 1) * (2 * e - 1) : 1;
            if (t >= 1) timer.Stop();
            if (Moved != null) Moved();
        }

        static float Lerp(float a, float b, float t) { return a + (b - a) * t; }

        // How much of the rectangle r the highlight covers now (0..1), faded with it.
        public float Cover(RectangleF r)
        {
            if (Now.IsEmpty) return 0;
            float top = Math.Max(Now.Top, r.Top), bottom = Math.Min(Now.Bottom, r.Bottom);
            return bottom <= top ? 0 : Math.Min(1f, (bottom - top) / r.Height) * Alpha;
        }
    }

    // Base for the hand-drawn controls: double-buffered, no flicker, follows the parent's background.
    class SoftControl : Control
    {
        protected bool hover;

        public SoftControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Font = Theme.F(9.5f);
            ForeColor = Theme.Text;
        }

        protected int S(int v) { return Theme.S(v); }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected void FocusRing(Graphics g, Rectangle r, int radius)
        {
            if (Focused && ShowFocusCues) Soft.Outline(g, Rectangle.Inflate(r, -1, -1), radius, Theme.Accent, 1.5f);
        }
    }

    // A rounded button: Primary is filled with the accent colour, the others are quiet outlines.
    class PillButton : SoftControl, IButtonControl
    {
        public bool Primary;
        public DialogResult DialogResult { get; set; }
        bool isDefault;

        public PillButton(string text, bool primary)
        {
            Text = text;
            Primary = primary;
            Cursor = Cursors.Hand;
            TabStop = true;
            Size = GetPreferredSize(Size.Empty);
        }

        public override Size GetPreferredSize(Size proposed)
        {
            Size t = TextRenderer.MeasureText(Text, Font);
            return new Size(t.Width + S(36), Math.Max(S(34), t.Height + S(14)));
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Size = GetPreferredSize(Size.Empty); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Soft.ParentBack(this));
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = Height / 2;
            Color text;
            if (Primary)
            {
                Color fill = !Enabled ? Theme.Border : (hover ? ControlPaint.Dark(Theme.Accent, 0.04f) : Theme.Accent);
                Soft.Fill(g, r, rad, fill);
                text = Theme.OnAccent;
            }
            else
            {
                Soft.Fill(g, r, rad, hover && Enabled ? Theme.Hover : Theme.Bg);
                Soft.Outline(g, r, rad, Theme.Border, 1);
                text = Enabled ? Theme.Text : Theme.Muted;
            }
            FocusRing(g, r, rad);
            TextRenderer.DrawText(g, Text, Font, r, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        protected override void OnClick(EventArgs e)
        {
            if (!Enabled) return;
            base.OnClick(e);
            var f = FindForm();
            if (f != null && DialogResult != DialogResult.None) f.DialogResult = DialogResult;
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Enter || k == Keys.Space || base.IsInputKey(k); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty); base.OnKeyUp(e); }

        public void NotifyDefault(bool value) { isDefault = value; }
        public void PerformClick() { OnClick(EventArgs.Empty); }
    }

    // An on/off switch in place of a check box.
    class ToggleSwitch : SoftControl
    {
        public event EventHandler CheckedChanged;
        bool on;

        public ToggleSwitch(bool initial)
        {
            on = initial;
            Cursor = Cursors.Hand;
            TabStop = true;
            Size = new Size(S(40), S(22));
        }

        public bool Checked
        {
            get { return on; }
            set { if (on == value) return; on = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Soft.ParentBack(this));
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Soft.Fill(g, r, Height / 2, on ? Theme.Accent : (Theme.Dark ? Color.FromArgb(0x4A, 0x47, 0x43) : Color.FromArgb(0xD9, 0xD6, 0xD1)));
            int k = Height - S(6);
            int x = on ? Width - k - S(3) : S(3);
            using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x, S(3), k, k);
            FocusRing(g, r, Height / 2);
        }

        protected override void OnClick(EventArgs e) { Checked = !Checked; Focus(); base.OnClick(e); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Space || base.IsInputKey(k); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyUp(e); }
    }

    // One of a few pills in a soft track: the choice of what the shortcut does.
    class Segmented : SoftControl
    {
        public event EventHandler SelectedChanged;
        readonly string[] items;
        Rectangle[] boxes = new Rectangle[0];
        int selected, hot = -1;

        public Segmented(string[] items, int selected)
        {
            this.items = items;
            this.selected = selected;
            Cursor = Cursors.Hand;
            TabStop = true;
            Size = GetPreferredSize(Size.Empty);
        }

        public int SelectedIndex
        {
            get { return selected; }
            set { if (value == selected) return; selected = value; Invalidate(); if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty); }
        }

        public override Size GetPreferredSize(Size proposed)
        {
            if (items == null) return base.GetPreferredSize(proposed);
            int w = S(6), h = 0;
            foreach (string s in items) { Size t = TextRenderer.MeasureText(s, Font); w += t.Width + S(26); h = Math.Max(h, t.Height); }
            return new Size(w, h + S(16));
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (items == null) return;   // the base constructor lays out before the items are set
            boxes = new Rectangle[items.Length];
            int x = S(3);
            for (int i = 0; i < items.Length; i++)
            {
                int w = TextRenderer.MeasureText(items[i], Font).Width + S(26);
                boxes[i] = new Rectangle(x, S(3), w, Height - S(7));
                x += w;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Soft.ParentBack(this));
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Soft.Fill(g, r, Height / 2, Theme.Card);
            for (int i = 0; i < items.Length && i < boxes.Length; i++)
            {
                if (i == selected)
                {
                    Soft.Fill(g, boxes[i], boxes[i].Height / 2, Theme.Bg);
                    Soft.Outline(g, boxes[i], boxes[i].Height / 2, Theme.Border, 1);
                }
                TextRenderer.DrawText(g, items[i], Font, boxes[i], i == selected ? Theme.Text : (i == hot ? Theme.Text : Theme.Muted),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            FocusRing(g, r, Height / 2);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1;
            for (int i = 0; i < boxes.Length; i++) if (boxes[i].Contains(e.Location)) h = i;
            if (h != hot) { hot = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { hot = -1; base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            Focus();
            for (int i = 0; i < boxes.Length; i++) if (boxes[i].Contains(e.Location)) SelectedIndex = i;
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left && selected > 0) SelectedIndex = selected - 1;
            if (e.KeyCode == Keys.Right && selected < items.Length - 1) SelectedIndex = selected + 1;
            base.OnKeyDown(e);
        }
    }

    // A selectable card: the AI service choice.
    class ChoiceCard : SoftControl
    {
        public string Title, Subtitle;
        bool selected;
        public event EventHandler Picked;

        public ChoiceCard(string title, string subtitle)
        {
            Title = title;
            Subtitle = subtitle;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }

        public int HeightFor(int width)
        {
            int t = TextRenderer.MeasureText(Title, Theme.FB(9.5f), new Size(width - S(24), int.MaxValue), TextFormatFlags.WordBreak).Height;
            int s = TextRenderer.MeasureText(Subtitle, Theme.F(8.5f), new Size(width - S(24), int.MaxValue), TextFormatFlags.WordBreak).Height;
            return S(10) + t + S(2) + s + S(10);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Soft.ParentBack(this));
            var r = new Rectangle(1, 1, Width - 3, Height - 3);
            int rad = S(10);
            if (selected) { Soft.Fill(g, r, rad, Theme.AccentSoft); Soft.Outline(g, r, rad, Theme.Accent, S(2) > 2 ? 2 : 1.5f); }
            else if (hover) Soft.Fill(g, r, rad, Theme.Hover);
            FocusRing(g, r, rad);
            using (var ft = Theme.FB(9.5f)) using (var fs = Theme.F(8.5f))
            {
                int th = TextRenderer.MeasureText(Title, ft, new Size(Width - S(24), int.MaxValue), TextFormatFlags.WordBreak).Height;
                TextRenderer.DrawText(g, Title, ft, new Rectangle(S(12), S(10), Width - S(24), th), selected ? Theme.Accent : Theme.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, Subtitle, fs, new Rectangle(S(12), S(10) + th + S(2), Width - S(24), Height), Theme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }
        }

        protected override void OnClick(EventArgs e) { Focus(); if (Picked != null) Picked(this, EventArgs.Empty); base.OnClick(e); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Space || base.IsInputKey(k); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) OnClick(EventArgs.Empty); base.OnKeyUp(e); }
    }

    // A rounded field around a borderless text box; the outline turns accent while typing.
    class SoftField : Panel
    {
        public readonly TextBox Box;
        bool active;

        public SoftField(TextBox box, int width, bool multiline)
        {
            Box = box;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.InputBg;
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Theme.InputBg;
            box.ForeColor = Theme.Text;
            box.Font = Theme.F(9.5f);
            box.Multiline = multiline;
            int pad = Theme.S(10);
            int h = multiline ? Theme.S(58) : box.PreferredHeight;
            Size = new Size(width, h + Theme.S(16));
            box.SetBounds(pad, Theme.S(8), width - 2 * pad, h);
            box.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            Controls.Add(box);
            box.Enter += delegate { active = true; Invalidate(); };
            box.Leave += delegate { active = false; Invalidate(); };
            Click += delegate { box.Focus(); };
            Cursor = Cursors.IBeam;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Soft.ParentBack(this));
            var r = new Rectangle(1, 1, Width - 3, Height - 3);
            int rad = Theme.S(10);
            Soft.Fill(g, r, rad, Enabled ? Theme.InputBg : Theme.Card);
            Soft.Outline(g, r, rad, active ? Theme.Accent : Theme.Border, active ? 1.5f : 1);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Box.BackColor = Enabled ? Theme.InputBg : Theme.Card;
            BackColor = Box.BackColor;
            Invalidate();
        }
    }

    // A white rounded card on the page that stacks its rows from top to bottom.
    class SoftCard : Panel
    {
        public SoftCard()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        // Separator lines between rows, at these y positions.
        public List<int> Dividers = new List<int>();

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Soft.ParentBack(this));
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Soft.Fill(g, r, Theme.S(14), Theme.Bg);
            Soft.Outline(g, r, Theme.S(14), Theme.Border, 1);
            using (var pen = new Pen(Theme.Border))
                foreach (int y in Dividers) g.DrawLine(pen, Theme.S(14), y, Width - Theme.S(14), y);
        }
    }
}
