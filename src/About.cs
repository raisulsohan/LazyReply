using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace LazyReply
{
    // "About LazyReply": the name, one line on what it does, and the author's credit with a link.
    class AboutForm : Form
    {
        public const string AuthorUrl = "https://raisulsohan.com/en/portfolio";

        static AboutForm open;

        // "Created by Raisul Sohan." with the name as a link to the author's portfolio.
        public static LinkLabel CreditLink()
        {
            const string text = "Created by Raisul Sohan.";
            const string name = "Raisul Sohan";
            var l = new LinkLabel { Text = text, AutoSize = true, LinkArea = new LinkArea(text.IndexOf(name), name.Length) };
            l.LinkClicked += delegate
            {
                try { Process.Start(new ProcessStartInfo(AuthorUrl) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Write("link failed: " + ex.Message); }
            };
            return l;
        }

        public static void ShowOnce(Icon icon)
        {
            if (open != null && !open.IsDisposed) { open.Activate(); return; }
            open = new AboutForm(icon);
            open.Show();
            Native.ForceForeground(open.Handle);
        }

        AboutForm(Icon icon)
        {
            Text = "About LazyReply";
            Icon = icon;
            Font = Theme.F(9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Theme.Page;
            ForeColor = Theme.Text;
            float sc;
            using (var g = CreateGraphics()) sc = g.DpiX / 96f;
            Func<int, int> S = v => (int)Math.Round(v * sc);

            var p = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false,
                Padding = new Padding(S(28), S(24), S(28), S(20)), BackColor = Theme.Page
            };
            var mark = new PictureBox { Image = Theme.Mark(S(52), Theme.Accent), Size = new Size(S(52), S(52)), Margin = new Padding(0, 0, 0, S(12)), BackColor = Theme.Page };
            var title = new Label { Text = "LazyReply", Font = Theme.FB(15f), AutoSize = true, Margin = new Padding(0, 0, 0, S(4)), BackColor = Theme.Page };
            var what = new Label
            {
                Text = "চ্যাটের বক্সে বাংলা বা Banglish লিখুন, বক্সের উপরেই গুছানো ইংরেজি পান।",
                AutoSize = true, MaximumSize = new Size(S(340), 0), ForeColor = Theme.Muted, Margin = new Padding(0, 0, 0, S(14)), BackColor = Theme.Page
            };
            var credit = CreditLink();
            credit.Margin = new Padding(0, 0, 0, S(20));
            credit.BackColor = Theme.Page;
            credit.ForeColor = Theme.Text;
            credit.LinkColor = Theme.Accent;
            credit.ActiveLinkColor = Theme.Accent;
            var ok = new PillButton("ঠিক আছে", true) { Margin = new Padding(0), BackColor = Theme.Page };
            ok.Click += delegate { Close(); };
            AcceptButton = ok;
            CancelButton = ok;
            p.Controls.AddRange(new Control[] { mark, title, what, credit, ok });
            Controls.Add(p);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Soft.Window(Handle, true);
        }
    }
}
