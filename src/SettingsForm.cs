using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace LazyReply
{
    // One line of a settings card: a title and a quiet explanation on the left, the control on the right.
    class SettingRow : Panel
    {
        readonly Label title, sub;
        readonly Control right;
        public bool TopLine;

        public SettingRow(string titleText, string subText, Control rightControl, int width)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Margin = new Padding(0);
            Width = width;
            title = Soft.Text(titleText, Theme.F(9.5f), Theme.Text, Theme.Bg);
            sub = Soft.Text(subText, Theme.F(8.5f), Theme.Muted, Theme.Bg);
            right = rightControl;
            Controls.Add(title);
            Controls.Add(sub);
            if (right != null) Controls.Add(right);
            Relayout();
        }

        public string Sub { get { return sub.Text; } set { sub.Text = value; Relayout(); } }

        public void Relayout()
        {
            int pad = Theme.S(14), gap = Theme.S(14);
            int rw = right == null ? 0 : right.Width;
            int textW = Width - 2 * pad - (rw > 0 ? rw + gap : 0);
            title.MaximumSize = new Size(textW, 0);
            sub.MaximumSize = new Size(textW, 0);
            sub.Visible = sub.Text.Length > 0;
            int th = title.PreferredHeight + (sub.Visible ? Theme.S(2) + sub.GetPreferredSize(new Size(textW, 0)).Height : 0);
            int h = Math.Max(th, right == null ? 0 : right.Height) + 2 * Theme.S(12);
            Height = h;
            int ty = (h - th) / 2;
            title.Location = new Point(pad, ty);
            sub.Location = new Point(pad, ty + title.PreferredHeight + Theme.S(2));
            if (right != null) right.Location = new Point(Width - pad - rw, (h - right.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Bg);
            if (TopLine) using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, Theme.S(14), 0, Width - Theme.S(14), 0);
        }
    }

    class SettingsForm : Form
    {
        public event Action Saved;

        readonly Settings s;
        readonly ChoiceCard[] providerCards;
        readonly TextBox tbKey, tbModel, tbHotkey, tbAbout, tbOwnKey, tbUrl;
        readonly SoftField fKey, fModel, fOwnKey;
        readonly ToggleSwitch swFollowAvro, swBangla, swStartup;
        readonly Segmented segOnBangla;
        readonly Label lblTest, lblModelInfo, lblAccount, lnkShowKey, lnkWhereKey, lblKeyInfo;
        readonly PillButton btnTest, btnAccount, btnModel, btnUrl;
        readonly Control accountBlock, keyBlock, urlBlock;
        readonly SettingRow ownKeyRow;
        readonly FlowLayoutPanel page;
        readonly string[] keys = new string[4], models = new string[4];
        readonly int inner;
        List<string> geminiChoices = new List<string>(), openAIChoices = new List<string>();
        string baseUrl;
        int shownProvider;

        // Gemini has no account mode: Google ended personal-account sign-in for Gemini CLI.
        // "openai": any service with OpenAI's API, at the address the user gives.
        static readonly string[] Providers = { "claude-cli", "claude", "gemini", "openai" };

        // Services with OpenAI's API: name, address, where to get a key (empty: no key needed).
        static readonly string[][] Services =
        {
            new[] { "OpenAI (ChatGPT)", "https://api.openai.com/v1", "https://platform.openai.com/api-keys" },
            new[] { "OpenRouter", "https://openrouter.ai/api/v1", "https://openrouter.ai/keys" },
            new[] { "Groq", "https://api.groq.com/openai/v1", "https://console.groq.com/keys" },
            new[] { "DeepSeek", "https://api.deepseek.com/v1", "https://platform.deepseek.com/api_keys" },
            new[] { "Ollama (এই কম্পিউটারে)", "http://localhost:11434/v1", "" },
            new[] { "LM Studio (এই কম্পিউটারে)", "http://localhost:1234/v1", "" }
        };

        static int S(int v) { return Theme.S(v); }

        public SettingsForm(Settings settings, KeyCombo avroKey)
        {
            s = settings;
            Text = "LazyReply · সেটিংস";
            Font = Theme.F(9.5f);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Page;
            ForeColor = Theme.Text;

            keys[1] = s.ClaudeKey;
            keys[2] = s.GeminiKey;
            keys[3] = s.OpenAIKey;
            models[0] = s.ClaudeCliModel;
            models[1] = s.ClaudeModel;
            models[2] = s.GeminiModel;
            models[3] = s.OpenAIModel;
            baseUrl = s.OpenAIBaseUrl;

            int pagePad = S(22), cardW = S(596);
            int W = cardW + 2 * pagePad;
            inner = cardW - 2 * S(16);

            page = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Dock = DockStyle.Fill,
                BackColor = Theme.Page, Padding = new Padding(pagePad, S(18), 0, S(10))
            };

            // ---------- header ----------
            var header = new Panel { BackColor = Theme.Page, Size = new Size(cardW, S(44)), Margin = new Padding(0, 0, 0, S(8)) };
            var mark = new PictureBox { Image = Theme.Mark(S(32), Theme.Accent), Size = new Size(S(32), S(32)), Location = new Point(0, S(4)), BackColor = Theme.Page };
            var title = Soft.Text("সেটিংস", Theme.FB(14f), Theme.Text, Theme.Page);
            title.Location = new Point(S(44), S(4));
            header.Controls.Add(mark);
            header.Controls.Add(title);
            page.Controls.Add(header);

            // ---------- AI service ----------
            page.Controls.Add(Caption("AI সার্ভিস"));
            var aiCard = Card(cardW);
            var aiFlow = CardFlow(aiCard);

            var choices = new Panel { BackColor = Theme.Bg, Width = inner, Margin = new Padding(0, 0, 0, S(10)) };
            providerCards = new[]
            {
                new ChoiceCard("Claude · অ্যাকাউন্ট", "key লাগে না · প্রায় ১.৫ সে."),
                new ChoiceCard("Claude · API key", "সবচেয়ে দ্রুত"),
                new ChoiceCard("Gemini · API key", "ফ্রি স্তর আছে"),
                new ChoiceCard("অন্য AI · API", "ChatGPT, OpenRouter, Groq, Ollama…")
            };
            int n = providerCards.Length;
            int cw = (inner - (n - 1) * S(8)) / n, ch = 0;
            foreach (var c in providerCards) ch = Math.Max(ch, c.HeightFor(cw));
            for (int i = 0; i < n; i++)
            {
                int idx = i;
                providerCards[i].SetBounds(i * (cw + S(8)), 0, cw, ch);
                providerCards[i].BackColor = Theme.Bg;
                providerCards[i].Picked += delegate { PickProvider(idx); };
                choices.Controls.Add(providerCards[i]);
            }
            choices.Height = ch;
            aiFlow.Controls.Add(choices);

            // Account (Claude Code) or API key, whichever the service needs.
            lblAccount = Note("", inner);
            btnAccount = new PillButton("", false) { BackColor = Theme.Bg };
            btnAccount.Click += delegate { AccountAction(); };
            accountBlock = Block(lblAccount, btnAccount);
            aiFlow.Controls.Add(accountBlock);

            // The address of a service with OpenAI's API: typed, or picked from the menu.
            tbUrl = new TextBox();
            btnUrl = new PillButton("বাছুন ▾", false) { BackColor = Theme.Bg };
            var fUrl = new SoftField(tbUrl, inner - btnUrl.Width - S(8), false);
            btnUrl.Click += delegate { ServiceMenu(); };
            tbUrl.TextChanged += delegate { if (shownProvider == 3) ShowKeyHelp(); };
            var urlLine = new Panel { BackColor = Theme.Bg, Size = new Size(inner, Math.Max(fUrl.Height, btnUrl.Height)), Margin = new Padding(0, 0, 0, S(4)) };
            fUrl.Location = new Point(0, (urlLine.Height - fUrl.Height) / 2);
            btnUrl.Location = new Point(inner - btnUrl.Width, (urlLine.Height - btnUrl.Height) / 2);
            urlLine.Controls.Add(fUrl);
            urlLine.Controls.Add(btnUrl);
            var urlInfo = Note("OpenAI-র মতো API আছে এমন যেকোনো সার্ভিস চলে। \"বাছুন\" থেকে নিন, অথবা সার্ভিসের দেওয়া ঠিকানা লিখুন।", inner);
            urlInfo.Margin = new Padding(S(4), S(2), 0, S(12));
            urlBlock = Block(Small("সার্ভারের ঠিকানা (base URL)"), urlLine, urlInfo);
            aiFlow.Controls.Add(urlBlock);

            tbKey = new TextBox { UseSystemPasswordChar = true };
            fKey = new SoftField(tbKey, inner, false) { Margin = new Padding(0, 0, 0, S(6)) };
            lnkShowKey = Link("key দেখাও", delegate { tbKey.UseSystemPasswordChar = !tbKey.UseSystemPasswordChar; lnkShowKey.Text = tbKey.UseSystemPasswordChar ? "key দেখাও" : "key লুকাও"; });
            lnkWhereKey = Link("key কোথায় পাবেন?", delegate { string u = KeyPage(); if (u.Length > 0) OpenUrl(u); });
            var keyLinks = new FlowLayoutPanel { AutoSize = true, BackColor = Theme.Bg, WrapContents = false, Margin = new Padding(S(4), 0, 0, S(10)) };
            lnkShowKey.Margin = new Padding(0, 0, S(16), 0);
            keyLinks.Controls.Add(lnkShowKey);
            keyLinks.Controls.Add(lnkWhereKey);
            lblKeyInfo = Note("", inner);
            keyBlock = Block(Small("API key"), fKey, keyLinks, lblKeyInfo);
            aiFlow.Controls.Add(keyBlock);

            // Model: a field with a menu of the usual choices; any name can also be typed.
            tbModel = new TextBox();
            btnModel = new PillButton("বাছুন ▾", false) { BackColor = Theme.Bg };
            fModel = new SoftField(tbModel, inner - btnModel.Width - S(8), false);
            btnModel.Click += delegate { ModelMenu(); };
            var modelLine = new Panel { BackColor = Theme.Bg, Size = new Size(inner, Math.Max(fModel.Height, btnModel.Height)), Margin = new Padding(0, 0, 0, S(4)) };
            fModel.Location = new Point(0, (modelLine.Height - fModel.Height) / 2);
            btnModel.Location = new Point(inner - btnModel.Width, (modelLine.Height - btnModel.Height) / 2);
            modelLine.Controls.Add(fModel);
            modelLine.Controls.Add(btnModel);
            lblModelInfo = Note("", inner);
            lblModelInfo.Margin = new Padding(S(4), S(2), 0, S(12));
            aiFlow.Controls.Add(Block(Small("মডেল"), modelLine, lblModelInfo));

            btnTest = new PillButton("পরীক্ষা করুন", false) { BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, S(6)) };
            btnTest.Click += delegate { RunTest(); };
            lblTest = Note("", inner);
            aiFlow.Controls.Add(btnTest);
            aiFlow.Controls.Add(lblTest);
            page.Controls.Add(aiCard);

            // ---------- use ----------
            page.Controls.Add(Caption("ব্যবহার"));
            var useCard = Card(cardW);
            var useFlow = CardFlow(useCard);
            useFlow.Padding = new Padding(0);
            int rowW = cardW - 2 * S(6);

            string avroKeyText = avroKey == null ? "Ctrl+Space" : avroKey.ToString();
            swFollowAvro = new ToggleSwitch(string.IsNullOrEmpty(s.OwnSwitchKey)) { BackColor = Theme.Bg };
            var rowFollow = new SettingRow("অভ্রের কী দিয়ে বাক্স খোলো",
                "এখন: " + avroKeyText + "। কীটা অভ্রের নিজের সেটিংস থেকে পড়া হয়, অভ্রে বদলালে এখানেও বদলায়।", swFollowAvro, rowW);
            useFlow.Controls.Add(rowFollow);

            tbOwnKey = new TextBox { Text = s.OwnSwitchKey };
            fOwnKey = new SoftField(tbOwnKey, S(170), false);
            ownKeyRow = new SettingRow("নিজের কী", "অভ্র না থাকলে বা অন্য কী চাইলে: যেমন Ctrl+Alt+T, F9। কীটা চ্যাট অ্যাপের কাছেও যায়।", fOwnKey, rowW) { TopLine = true };
            useFlow.Controls.Add(ownKeyRow);
            swFollowAvro.CheckedChanged += delegate { ownKeyRow.Visible = !swFollowAvro.Checked; if (!swFollowAvro.Checked) tbOwnKey.Focus(); };
            ownKeyRow.Visible = !swFollowAvro.Checked;

            segOnBangla = new Segmented(new[] { "টিপে জিজ্ঞেস", "বাক্স খোলো", "কিছু না" }, Math.Max(0, Math.Min(2, s.OnBanglaMode))) { BackColor = Theme.Bg };
            useFlow.Controls.Add(new SettingRow("শর্টকাট চাপলে", "", segOnBangla, rowW) { TopLine = true });

            tbHotkey = new TextBox { Text = s.PanelHotkey };
            var fHotkey = new SoftField(tbHotkey, S(170), false);
            useFlow.Controls.Add(new SettingRow("আলাদা উইন্ডোর কী", "বড় লেখা আর উল্টো অনুবাদের জন্য আলাদা উইন্ডো খোলে।", fHotkey, rowW) { TopLine = true });

            swBangla = new ToggleSwitch(s.OfferBangla) { BackColor = Theme.Bg };
            useFlow.Controls.Add(new SettingRow("ইংরেজি লিখলে বাংলা অনুবাদ", "চতুর্থ লাইনে আসে (Alt+4)। চালু থাকলে ইংরেজিতে ফিরলেও বাক্স খোলা থাকে।", swBangla, rowW) { TopLine = true });

            swStartup = new ToggleSwitch(Settings.StartWithWindows) { BackColor = Theme.Bg };
            useFlow.Controls.Add(new SettingRow("Windows চালু হলে নিজে চালু", "", swStartup, rowW) { TopLine = true });
            page.Controls.Add(useCard);

            // ---------- about you ----------
            page.Controls.Add(Caption("আপনার পরিচয় (ঐচ্ছিক)"));
            var meCard = Card(cardW);
            var meFlow = CardFlow(meCard);
            tbAbout = new TextBox { Text = s.AboutMe };
            meFlow.Controls.Add(new SoftField(tbAbout, inner, true) { Margin = new Padding(0, 0, 0, S(6)) });
            meFlow.Controls.Add(Note("ইংরেজিতে, যেমন: I am a video editor and motion designer. এতে শব্দ বাছাই ভালো হয়।", inner));
            page.Controls.Add(meCard);

            // ---------- about ----------
            page.Controls.Add(Caption("About"));
            var aboutCard = Card(cardW);
            var aboutFlow = CardFlow(aboutCard);
            var credit = AboutForm.CreditLink();
            credit.BackColor = Theme.Bg;
            credit.LinkColor = Theme.Accent;
            credit.ActiveLinkColor = Theme.Accent;
            credit.ForeColor = Theme.Text;
            aboutFlow.Controls.Add(credit);
            page.Controls.Add(aboutCard);

            // ---------- footer ----------
            var footer = new Panel { Dock = DockStyle.Bottom, Size = new Size(W, S(64)), BackColor = Theme.Page };   // its width first: the buttons anchor to the right edge
            footer.Paint += (o, e) => { using (var pen = new Pen(Theme.Border)) e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0); };
            var ok = new PillButton("সেভ করুন", true) { BackColor = Theme.Page };
            var cancel = new PillButton("বাতিল", false) { BackColor = Theme.Page, DialogResult = DialogResult.Cancel };
            ok.Click += delegate { if (SaveAll()) Close(); };
            cancel.Click += delegate { Close(); };
            ok.Location = new Point(W - pagePad - ok.Width, (footer.Height - ok.Height) / 2);
            cancel.Location = new Point(ok.Left - S(10) - cancel.Width, (footer.Height - cancel.Height) / 2);
            ok.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            footer.Controls.Add(ok);
            footer.Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(page);
            Controls.Add(footer);

            shownProvider = Math.Max(0, Array.IndexOf(Providers, s.Provider));
            ShowProvider();

            int contentH = page.GetPreferredSize(new Size(W, 0)).Height;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int maxH = wa.Height - S(80);
            bool scrolls = contentH + footer.Height + S(10) > maxH;
            ClientSize = new Size(W + (scrolls ? SystemInformation.VerticalScrollBarWidth : 0), Math.Min(contentH + footer.Height + S(10), maxH));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Soft.Window(Handle, true);
        }

        // ---------- building blocks ----------

        Label Caption(string text)
        {
            var l = Soft.Text(text, Theme.F(8.5f), Theme.Muted, Theme.Page);
            l.Margin = new Padding(S(4), S(12), 0, S(6));
            return l;
        }

        static SoftCard Card(int width)
        {
            return new SoftCard { Width = width, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(S(6)), Margin = new Padding(0, 0, 0, S(4)) };
        }

        FlowLayoutPanel CardFlow(SoftCard card)
        {
            var f = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Theme.Bg, Padding = new Padding(S(10), S(8), S(10), S(6)), Margin = new Padding(0),
                MinimumSize = new Size(card.Width - 2 * S(6), 0)
            };
            card.Controls.Add(f);
            return f;
        }

        Control Block(params Control[] items)
        {
            var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Bg, Margin = new Padding(0) };
            foreach (var c in items) f.Controls.Add(c);
            return f;
        }

        Label Small(string text)
        {
            var l = Soft.Text(text, Theme.F(8.5f), Theme.Muted, Theme.Bg);
            l.Margin = new Padding(S(4), S(4), 0, S(4));
            return l;
        }

        Label Note(string text, int width)
        {
            var l = Soft.Text(text, Theme.F(8.5f), Theme.Muted, Theme.Bg);
            l.MaximumSize = new Size(width, 0);
            l.Margin = new Padding(S(4), 0, 0, S(8));
            return l;
        }

        Label Link(string text, EventHandler click)
        {
            var l = Soft.Text(text, Theme.F(8.5f), Theme.Accent, Theme.Bg);
            l.Cursor = Cursors.Hand;
            l.Click += click;
            return l;
        }

        // ---------- AI service ----------

        void PickProvider(int i)
        {
            if (i == shownProvider) return;
            StashProvider();
            shownProvider = i;
            ShowProvider();
        }

        void StashProvider()
        {
            if (shownProvider != 0) keys[shownProvider] = tbKey.Text.Trim();
            models[shownProvider] = tbModel.Text.Trim();
            if (shownProvider == 3) baseUrl = tbUrl.Text.Trim();
        }

        void ShowProvider()
        {
            int i = shownProvider;
            for (int k = 0; k < providerCards.Length; k++) providerCards[k].Selected = k == i;
            bool account = i == 0;
            lblTest.Text = "";
            accountBlock.Visible = account;
            keyBlock.Visible = !account;
            urlBlock.Visible = i == 3;
            if (i == 3) tbUrl.Text = baseUrl ?? "";
            tbKey.Text = account ? "" : (keys[i] ?? "");
            ShowKeyHelp();
            if (i == 0)
            {
                tbModel.Text = string.IsNullOrEmpty(models[0]) ? "sonnet" : models[0];
                lblModelInfo.Text = "sonnet: Banglish ভালো বোঝে। haiku: প্ল্যানের সীমা কম খরচ হয়, কিন্তু Banglish-এর মানে মাঝে মাঝে ভুল করে। opus: সবচেয়ে ভালো, কিন্তু ধীর।";
            }
            else if (i == 1)
            {
                tbModel.Text = string.IsNullOrEmpty(models[1]) ? Settings.ClaudeModels[0] : models[1];
                lblModelInfo.Text = "claude-opus-5-5: সবচেয়ে ভালো লেখা। claude-haiku-5-5: সবচেয়ে দ্রুত ও সস্তা, চ্যাটের জন্য যথেষ্ট।";
            }
            else if (i == 2)
            {
                tbModel.Text = models[2] ?? "";
                lblModelInfo.Text = "ফ্রি স্তরে শুধু \"flash\" মডেলগুলো চলে। ফাঁকা রাখলে \"পরীক্ষা করুন\" চাপলে নতুনতম Flash মডেল নিজে বেছে নেওয়া হয়। একটার সীমা শেষ হলে অ্যাপ নিজে অন্যটায় যায়।";
            }
            else
            {
                tbModel.Text = models[3] ?? "";
                lblModelInfo.Text = "সার্ভিসের মডেলের নাম লিখুন। অথবা \"পরীক্ষা করুন\" চাপুন: সার্ভিসের মডেলের তালিকা \"বাছুন\"-এ আসবে। দ্রুত আর সস্তা মডেলই চ্যাটের জন্য যথেষ্ট।";
            }
            ShowAccount();
        }

        string[] ModelChoices()
        {
            if (shownProvider == 0) return new[] { "sonnet", "haiku", "opus" };
            if (shownProvider == 1) return Settings.ClaudeModels;
            List<string> found = shownProvider == 2 ? geminiChoices : openAIChoices;
            if (found.Count > 0) return found.Take(40).ToArray();
            return string.IsNullOrEmpty(tbModel.Text) ? new[] { "(আগে \"পরীক্ষা করুন\" চাপুন)" } : new[] { tbModel.Text };
        }

        void ServiceMenu()
        {
            var menu = new ContextMenuStrip { Font = Theme.F(9.5f), Renderer = Soft.MenuRenderer(), ShowImageMargin = false };
            foreach (string[] sv in Services)
            {
                string url = sv[1];
                var item = new ToolStripMenuItem(sv[0] + "   " + url) { Checked = Same(url, tbUrl.Text) };
                item.Click += delegate
                {
                    if (Same(url, tbUrl.Text)) return;
                    tbUrl.Text = url;
                    // Another service: its models and key are not the old one's.
                    tbModel.Text = "";
                    openAIChoices.Clear();
                    lblTest.Text = "";
                };
                menu.Items.Add(item);
            }
            menu.Show(btnUrl, new Point(0, btnUrl.Height + S(4)));
        }

        static bool Same(string a, string b)
        {
            return string.Equals((a ?? "").Trim().TrimEnd('/'), (b ?? "").Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        // The page where the shown service gives keys; empty when it is not known.
        string KeyPage()
        {
            if (shownProvider == 1) return "https://console.anthropic.com/settings/keys";
            if (shownProvider == 2) return "https://aistudio.google.com/apikey";
            foreach (string[] sv in Services) if (Same(sv[1], tbUrl.Text)) return sv[2];
            return "";
        }

        void ShowKeyHelp()
        {
            bool local = shownProvider == 3 && Settings.IsLocalUrl(tbUrl.Text);
            lnkWhereKey.Visible = KeyPage().Length > 0;
            lblKeyInfo.Text = shownProvider != 3 ? ""
                : local ? "এই কম্পিউটারের সার্ভারে key লাগে না। ঘরটা ফাঁকা রাখুন।"
                : Same(tbUrl.Text, Services[0][1]) ? "ChatGPT Plus-এর মাসিক প্ল্যানে API নেই। platform.openai.com-এ আলাদা credit কিনতে হয়।"
                : "";
            lblKeyInfo.Visible = lblKeyInfo.Text.Length > 0;
        }

        void ModelMenu()
        {
            var menu = new ContextMenuStrip { Font = Theme.F(9.5f), Renderer = Soft.MenuRenderer(), ShowImageMargin = false };
            foreach (string m in ModelChoices())
            {
                string name = m;
                var item = new ToolStripMenuItem(name) { Checked = name == tbModel.Text.Trim() };
                if (name.StartsWith("(")) item.Enabled = false;
                else item.Click += delegate { tbModel.Text = name; };
                menu.Items.Add(item);
            }
            menu.Show(btnModel, new Point(0, btnModel.Height + S(4)));
        }

        void ShowAccount()
        {
            if (shownProvider != 0) return;
            string exe = Engine.FindClaudeCli();
            lblAccount.Text = exe != null
                ? "Claude Code পাওয়া গেছে। আপনার Claude প্ল্যান (Pro/Max) ব্যবহার হবে, আর এর সীমা Claude Code-এর সাথে ভাগ হবে।"
                : "Claude Code পাওয়া যায়নি। ইনস্টল করে login করুন, অথবা API key বাছুন।";
            btnAccount.Text = exe != null ? "Claude Code-এ login দেখুন" : "Claude Code ইনস্টলের পাতা খুলুন";
            btnAccount.Margin = new Padding(0, 0, 0, S(12));
        }

        // Opens a console window that the user drives: signing in stays in the user's hands.
        void AccountAction()
        {
            if (shownProvider != 0) return;
            if (Engine.FindClaudeCli() == null) { OpenUrl("https://code.claude.com/docs"); return; }
            OpenConsole("claude /login");
        }

        static void OpenConsole(string command)
        {
            try
            {
                Process.Start(new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/k " + command)
                {
                    UseShellExecute = true, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                });
            }
            catch (Exception ex) { Log.Write("console failed: " + ex.Message); }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            ShowAccount();   // the user may have just installed or signed in
        }

        Settings Draft()
        {
            StashProvider();
            var d = s.Clone();
            d.Provider = Providers[shownProvider];
            d.ClaudeKey = keys[1];
            d.GeminiKey = keys[2];
            d.OpenAIKey = keys[3];
            d.ClaudeCliModel = string.IsNullOrEmpty(models[0]) ? "sonnet" : models[0];
            d.ClaudeModel = string.IsNullOrEmpty(models[1]) ? Settings.ClaudeModels[0] : models[1];
            d.GeminiModel = models[2] ?? "";
            d.OpenAIModel = models[3] ?? "";
            d.OpenAIBaseUrl = string.IsNullOrEmpty(baseUrl) ? Services[0][1] : baseUrl;
            d.AboutMe = tbAbout.Text.Trim();
            d.OfferBangla = swBangla.Checked;
            return d;
        }

        async void RunTest()
        {
            var d = Draft();
            if (d.NeedsKey && string.IsNullOrEmpty(d.ActiveKey)) { ShowTest("আগে API key দিন।", true); return; }
            btnTest.Enabled = false;
            ShowTest("পরীক্ষা চলছে…", false);
            try
            {
                if (d.Provider == "openai")
                {
                    // The model list fills the menu. Some services have no list: then a typed name is enough.
                    try { openAIChoices = await Engine.ListOpenAIModels(d, CancellationToken.None); }
                    catch (EngineException ex)
                    {
                        if (string.IsNullOrEmpty(d.OpenAIModel)) throw;
                        Log.Write("model list failed: " + ex.Status);
                    }
                    if (string.IsNullOrEmpty(d.OpenAIModel))
                    {
                        ShowTest(openAIChoices.Count > 0
                            ? "সংযোগ ঠিক আছে। এখন \"বাছুন\" থেকে একটা মডেল নিন, তারপর আবার \"পরীক্ষা করুন\" চাপুন।"
                            : "সংযোগ ঠিক আছে, কিন্তু সার্ভিস কোনো মডেলের নাম দেয়নি। মডেলের নাম নিজে লিখুন।", false);
                        btnTest.Enabled = true;
                        return;
                    }
                }
                if (d.Provider == "gemini")
                {
                    List<string> available = await Engine.ListGeminiModels(d.GeminiKey, CancellationToken.None);
                    // The free "flash" models first: the others often have no free quota.
                    geminiChoices = Engine.PickGeminiModels(available);
                    foreach (string m in available) if (!geminiChoices.Contains(m)) geminiChoices.Add(m);
                    if (string.IsNullOrEmpty(d.GeminiModel) || !available.Contains(d.GeminiModel))
                    {
                        d.GeminiModel = Engine.PickGeminiModel(available) ?? "";
                        models[2] = d.GeminiModel;
                        tbModel.Text = models[2];
                    }
                }
                Suggestions r = await Engine.Run(d, "ভাই কাজটা কাল সন্ধ্যার মধ্যে দিয়ে দিব, লোগোর ফাইলটা পাঠান", "", false, CancellationToken.None);
                string note = "";
                if (d.Provider == "gemini" && Engine.LastGeminiModel != null && Engine.LastGeminiModel != d.GeminiModel)
                {
                    note = d.GeminiModel + " চলেনি (ফ্রি স্তরে নেই বা সীমা শেষ), তাই " + Engine.LastGeminiModel + " বেছে নিয়েছি। ";
                    models[2] = Engine.LastGeminiModel;
                    tbModel.Text = models[2];
                }
                ShowTest(note + "কাজ করছে। নমুনা: " + r.Professional, false);
            }
            catch (Exception ex)
            {
                ShowTest(ex is EngineException ? ex.Message : "সমস্যা: " + ex.Message, true);
            }
            btnTest.Enabled = true;
        }

        void ShowTest(string text, bool error)
        {
            lblTest.Text = text;
            lblTest.ForeColor = error ? Theme.Danger : (text.StartsWith("কাজ করছে") || text.Contains("কাজ করছে") ? Theme.Bangla : Theme.Muted);
        }

        bool SaveAll()
        {
            if (!swFollowAvro.Checked && KeyCombo.Parse(tbOwnKey.Text) == null)
            {
                MessageBox.Show(this, "অনুবাদের বাক্স খোলার নিজের কী বোঝা যায়নি। এভাবে লিখুন: Ctrl+Alt+T বা F9", "LazyReply");
                return false;
            }
            if (KeyCombo.Parse(tbHotkey.Text) == null)
            {
                MessageBox.Show(this, "আলাদা উইন্ডোর কী বোঝা যায়নি। এভাবে লিখুন: Ctrl+Shift+E", "LazyReply");
                return false;
            }
            var d = Draft();
            s.Provider = d.Provider;
            s.ClaudeKeyEnc = d.ClaudeKeyEnc;
            s.GeminiKeyEnc = d.GeminiKeyEnc;
            s.OpenAIKeyEnc = d.OpenAIKeyEnc;
            s.ClaudeModel = d.ClaudeModel;
            s.GeminiModel = d.GeminiModel;
            s.OpenAIModel = d.OpenAIModel;
            s.OpenAIBaseUrl = d.OpenAIBaseUrl;
            s.ClaudeCliModel = d.ClaudeCliModel;
            s.AboutMe = d.AboutMe;
            s.OfferBangla = d.OfferBangla;
            s.OnBanglaMode = segOnBangla.SelectedIndex;
            s.PanelHotkey = KeyCombo.Parse(tbHotkey.Text).ToString();
            s.OwnSwitchKey = swFollowAvro.Checked ? "" : KeyCombo.Parse(tbOwnKey.Text).ToString();
            s.Save();
            if (swStartup.Checked != Settings.StartWithWindows) Settings.StartWithWindows = swStartup.Checked;
            if (Saved != null) Saved();
            return true;
        }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }
}
