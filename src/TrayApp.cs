using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Drawing;
using System.Windows.Forms;

namespace LazyReply
{
    // Lives in the notification area and connects the parts:
    // Avro watcher -> tip -> inline translation in the chat box (or the separate translator panel).
    class TrayApp : ApplicationContext
    {
        readonly Settings settings;
        readonly AvroWatcher avro;
        readonly NotifyIcon tray;
        readonly TranslatorPanel panel;
        readonly PromptToast toast;
        readonly InlineController inline;
        readonly Timer fgTimer;
        readonly uint myPid;
        readonly Icon iconEnglish, iconBangla, iconPaused;
        readonly ToolStripMenuItem miPause, miPausedList, miInline, miOpen, miAsk, miAuto, miOff;
        SettingsForm settingsForm;
        IntPtr lastExternal;
        // A pause belongs to one place: the app in front, or in a browser the website in front
        // (see Places). In a paused place nothing opens the translation box by itself (Avro's switch
        // key, the user's own key, the tip). A click on the tray icon pauses or resumes the place the
        // user is in; other places go on as before. The list is saved: a restart keeps it.
        Place here;          // the place of lastExternal, as last read
        IntPtr hereHwnd;
        int placeTicks;

        bool IsPaused(Place p) { return p != null && settings.PausedPlaces.Contains(p.Key); }
        bool Paused { get { return IsPaused(here); } }

        public TrayApp()
        {
            Theme.Load();
            settings = Settings.Load();
            myPid = (uint)Process.GetCurrentProcess().Id;

            // The first control installs the UI synchronization context that the watcher posts to.
            panel = new TranslatorPanel(settings);
            panel.SettingsRequested += ShowSettings;
            IntPtr unused = panel.Handle;
            toast = new PromptToast();
            toast.OpenClicked += SwitchOnInline;
            toast.DismissForever += OnTipDismissedForever;

            avro = new AvroWatcher();
            avro.PanelKey = KeyCombo.Parse(settings.PanelHotkey) ?? KeyCombo.Parse("Ctrl+Shift+E");
            avro.AcceptInjectedSwitch = Engine.Fake;
            avro.ModeChanged += OnModeChanged;
            avro.PanelHotkey += TogglePanel;
            avro.OwnKeyCombo = KeyCombo.Parse(settings.OwnSwitchKey);
            avro.OwnKey += delegate { if (!panel.IsActiveWindow) OnBoxKey(); };
            avro.AltKey += delegate(int n) { if (n == 0 && toast.Visible) SwitchOnInline(); };
            toast.VisibleChanged += delegate { avro.CaptureAltY = toast.Visible; };

            inline = new InlineController(settings, avro, myPid);
            inline.ActiveChanged += delegate { RefreshMenu(); RefreshTray(); };

            int size = SystemInformation.SmallIconSize.Width;
            iconEnglish = Icon.FromHandle(Theme.Mark(size, Theme.Accent).GetHicon());
            iconBangla = Icon.FromHandle(Theme.Mark(size, Theme.Bangla).GetHicon());
            iconPaused = Icon.FromHandle(Theme.Mark(size, Theme.Muted).GetHicon());

            var menu = new ContextMenuStrip { Font = Theme.F(9.5f), Renderer = Soft.MenuRenderer() };
            miPause = new ToolStripMenuItem("এখানে বিরতি", null, delegate { TogglePause(); });
            miPausedList = new ToolStripMenuItem("বিরতিতে থাকা জায়গা");
            miInline = new ToolStripMenuItem("অনুবাদের বাক্স", null, delegate
            {
                if (inline.Active) { inline.Deactivate(); return; }
                if (Paused) SetPaused(here, false);   // the user asked for the box here: that ends the pause here
                inline.Open();
            }) { Font = Theme.FB(9.5f) };
            miOpen = new ToolStripMenuItem("", null, delegate { OpenPanel(); });
            var when = new ToolStripMenuItem("শর্টকাট চাপলে");
            miAsk = new ToolStripMenuItem("আগে ছোট টিপে জিজ্ঞেস করো", null, delegate { SetOnBangla(OnBangla.Ask); });
            miAuto = new ToolStripMenuItem("অনুবাদের বাক্স খোলো", null, delegate { SetOnBangla(OnBangla.AutoOpen); });
            miOff = new ToolStripMenuItem("কিছু করো না", null, delegate { SetOnBangla(OnBangla.Off); });
            when.DropDownItems.AddRange(new ToolStripItem[] { miAsk, miAuto, miOff });
            menu.Items.AddRange(new ToolStripItem[]
            {
                miPause, miPausedList, new ToolStripSeparator(), miInline, miOpen, new ToolStripSeparator(), when,
                new ToolStripMenuItem("সেটিংস…", null, delegate { ShowSettings(); }),
                new ToolStripMenuItem("About LazyReply", null, delegate { AboutForm.ShowOnce(iconEnglish); }),
                new ToolStripSeparator(),
                new ToolStripMenuItem("LazyReply বন্ধ করুন", null, delegate { Quit(); })
            });

            tray = new NotifyIcon { Icon = iconEnglish, Visible = true, ContextMenuStrip = menu };
            // A click pauses LazyReply in the app or website the user is in, or resumes it there and
            // opens the translation box in the chat.
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) TogglePause(); };
            RefreshMenu();
            RefreshTray();

            // Remember the last window that was not ours: that is the chat box to paste into.
            fgTimer = new Timer { Interval = 250 };
            fgTimer.Tick += delegate
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg != IntPtr.Zero && Native.ProcessOf(fg) != myPid && !IsTaskbar(fg)) lastExternal = fg;
                // A new window: read its place at once. A browser: read the address bar twice a second,
                // since a tab or a link changes the website without a new window.
                placeTicks++;
                bool browser = here != null && (here.Key.StartsWith("web:") || IsBrowserKey(here));
                if (lastExternal != hereHwnd || (browser && placeTicks % 2 == 0)) TrackPlace();
            };
            fgTimer.Start();

            bool ready = settings.Provider == "claude-cli" ? Engine.FindClaudeCli() != null
                : !settings.NeedsKey || !string.IsNullOrEmpty(settings.ActiveKey);
            if (!ready && !Engine.Fake)
            {
                tray.ShowBalloonTip(6000, "LazyReply চালু হয়েছে", "শুরু করতে AI সার্ভিস ঠিক করুন। সেটিংস খুলছে।", ToolTipIcon.Info);
                var t = new Timer { Interval = 600 };
                t.Tick += delegate { t.Stop(); t.Dispose(); ShowSettings(); };
                t.Start();
            }
            else
            {
                tray.ShowBalloonTip(5000, "LazyReply চালু হয়েছে",
                    "চ্যাটের বক্সে ক্লিক করে Ctrl+Space চাপুন: অভ্রের ভাষা বদলাবে, আর অনুবাদের বাক্স খুলবে। বন্ধ করতে বাক্সের × চাপুন।", ToolTipIcon.Info);
            }
            Log.Write("started");
            Engine.Warm(settings);   // account mode: start Claude Code now, not at the first message
        }

        static bool IsBrowserKey(Place p)
        {
            return p != null && p.Key.StartsWith("app:") && Places.IsBrowser(p.Key.Substring(4));
        }

        async void TrackPlace()
        {
            IntPtr hwnd = lastExternal;
            if (hwnd == IntPtr.Zero) return;
            Place p = await Places.OfAsync(hwnd, 1500);
            if (p == null || hwnd != lastExternal) return;
            hereHwnd = hwnd;
            SetHere(p);
        }

        void SetHere(Place p)
        {
            bool changed = here == null || here.Key != p.Key;
            here = p;
            if (!changed) return;
            inline.Suspended = Paused;
            if (Paused) toast.HideToast();
            RefreshMenu();
            RefreshTray();
        }

        // Before a key opens the box: the place in front now, not the one read a moment ago.
        async Task<bool> PausedNow()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || Native.ProcessOf(fg) == myPid || IsTaskbar(fg)) return Paused;
            Place p = await Places.OfAsync(fg, 600);
            if (p != null) { lastExternal = fg; hereHwnd = fg; SetHere(p); }
            return Paused;
        }

        string HotkeyText { get { return avro.PanelKey == null ? "" : avro.PanelKey.ToString(); } }

        async void OnModeChanged(bool bangla, bool fromSwitchKey)
        {
            RefreshTray();
            if (panel.IsActiveWindow || await PausedNow()) return;
            bool ownKey = avro.OwnKeyCombo != null;   // then the user's own key opens the box, not Avro's
            if (settings.OnBangla == OnBangla.AutoOpen)
            {
                // The box follows the keyboard: Bangla opens it; English closes it, unless the user wants
                // their English translated to Bangla too. The "×" closes it at any time.
                if (fromSwitchKey && ownKey) return;
                if (bangla || settings.OfferBangla) { if (fromSwitchKey || !inline.Active) inline.Open(); }
                else inline.Deactivate();
                return;
            }
            if (!bangla) { toast.HideToast(); if (!ownKey) inline.Deactivate(); return; }
            if (inline.Active || settings.OnBangla != OnBangla.Ask || (ownKey && fromSwitchKey)) return;
            ShowTip();
        }

        // The user's own key: it switches their keyboard too, so each press opens or closes the box.
        async void OnBoxKey()
        {
            if (await PausedNow()) return;
            if (settings.OnBangla == OnBangla.AutoOpen) { if (inline.Active) inline.Deactivate(); else inline.Open(); }
            else if (settings.OnBangla == OnBangla.Ask && !inline.Active) ShowTip();
        }

        void ShowTip()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || Native.ProcessOf(fg) == myPid) fg = lastExternal;
            if (fg == IntPtr.Zero) { Log.Write("tip skipped: no chat window"); return; }
            Rectangle caret = Native.CaretRect(fg);
            if (caret == Rectangle.Empty)
            {
                Rectangle win = Native.WindowRect(fg);
                caret = win != Rectangle.Empty
                    ? new Rectangle(win.Left + win.Width / 2 - 150, win.Bottom - 90, 2, 20)
                    : new Rectangle(Cursor.Position, new Size(2, 20));
            }
            toast.ShowNear(caret, "Alt+Y");
        }

        void SwitchOnInline()
        {
            toast.HideToast();
            inline.Activate();
        }

        // The taskbar and its tray pop-ups take the focus when the tray icon is clicked; they are never the chat.
        static bool IsTaskbar(IntPtr hwnd)
        {
            string c = Native.ClassOf(hwnd);
            return c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd" || c == "NotifyIconOverflowWindow"
                || c == "TopLevelWindowForOverflowXamlIsland" || c == "Windows.UI.Core.CoreWindow";
        }

        // Pauses or resumes only the place the user is in: the app, or in a browser the website.
        async void TogglePause()
        {
            IntPtr target = lastExternal;
            Place p = await Places.OfAsync(target, 1500);
            if (p == null) p = here;
            else if (target == lastExternal) { hereHwnd = target; SetHere(p); }
            if (p == null)
            {
                tray.ShowBalloonTip(3500, "LazyReply", "কোন অ্যাপে আছেন, তা বোঝা যায়নি। চ্যাটের বক্সে একবার ক্লিক করে আবার চেষ্টা করুন।", ToolTipIcon.Info);
                return;
            }
            if (!IsPaused(p))
            {
                SetPaused(p, true);
                tray.ShowBalloonTip(3000, "বিরতি: " + p.Name,
                    "এখানে কোনো অনুবাদ আসবে না। অন্য অ্যাপ আর সাইটে LazyReply আগের মতো চলবে। এখানে আবার চালু করতে আইকনে ক্লিক করুন।", ToolTipIcon.Info);
                return;
            }
            SetPaused(p, false);
            // The tray click made the taskbar the active window: give the chat window its focus back.
            if (target != IntPtr.Zero && Native.IsWindow(target)) Native.ForceForeground(target);
            inline.Open();
            tray.ShowBalloonTip(3500, "অনুবাদ চালু: " + p.Name, "চ্যাটের বক্সে বাংলা বা Banglish লিখুন। দাঁড়ি (।) বা ? দিলে বক্সের উপরে ইংরেজি আসবে।", ToolTipIcon.Info);
        }

        void SetPaused(Place p, bool value) { SetPaused(p.Key, value); }

        void SetPaused(string key, bool value)
        {
            var list = settings.PausedPlaces;
            if (list.Contains(key) == value) return;
            if (value) list.Add(key); else list.Remove(key);
            settings.Save();
            Log.Write((value ? "paused " : "resumed ") + key);
            inline.Suspended = Paused;
            if (Paused) toast.HideToast();
            RefreshMenu();
            RefreshTray();
        }

        void ResumeEverywhere()
        {
            if (settings.PausedPlaces.Count == 0) return;
            settings.PausedPlaces.Clear();
            settings.Save();
            Log.Write("resumed everywhere");
            inline.Suspended = false;
            RefreshMenu();
            RefreshTray();
        }

        void OpenPanel()
        {
            toast.HideToast();
            IntPtr fg = Native.GetForegroundWindow();
            IntPtr target = fg != IntPtr.Zero && Native.ProcessOf(fg) != myPid ? fg : lastExternal;
            panel.OpenFor(target);
        }

        void TogglePanel()
        {
            if (panel.IsActiveWindow) panel.HidePanel(true);
            else OpenPanel();
        }

        void OnTipDismissedForever()
        {
            SetOnBangla(OnBangla.Off);
            tray.ShowBalloonTip(7000, "টিপটি বন্ধ হলো",
                "আবার চালু করতে: নিচের কোণে LazyReply আইকনে ডান-ক্লিক → \"শর্টকাট চাপলে\" → \"অনুবাদের বাক্স খোলো\"। সেটিংস থেকেও করা যায়।",
                ToolTipIcon.Info);
        }

        void SetOnBangla(OnBangla mode)
        {
            settings.OnBangla = mode;
            settings.Save();
            RefreshMenu();
        }

        void RefreshMenu()
        {
            miPause.Text = (here == null ? "এখানে বিরতি" : "বিরতি: " + here.Name) + "   (আইকনে এক ক্লিক)";
            miPause.Checked = Paused;
            miPausedList.DropDownItems.Clear();
            foreach (string key in settings.PausedPlaces.ToArray())
            {
                string k = key;
                miPausedList.DropDownItems.Add(new ToolStripMenuItem(Place.NameOf(k) + "  ·  চালু করো", null, delegate { SetPaused(k, false); }));
            }
            if (settings.PausedPlaces.Count > 0)
            {
                miPausedList.DropDownItems.Add(new ToolStripSeparator());
                miPausedList.DropDownItems.Add(new ToolStripMenuItem("সব জায়গায় চালু করো", null, delegate { ResumeEverywhere(); }));
            }
            miPausedList.Text = "বিরতিতে থাকা জায়গা (" + settings.PausedPlaces.Count + ")";
            miPausedList.Enabled = settings.PausedPlaces.Count > 0;
            miInline.Checked = inline.Active;
            miOpen.Text = "আলাদা অনুবাদক উইন্ডো" + (HotkeyText.Length > 0 ? "   (" + HotkeyText + ")" : "");
            miAsk.Checked = settings.OnBangla == OnBangla.Ask;
            miAuto.Checked = settings.OnBangla == OnBangla.AutoOpen;
            miOff.Checked = settings.OnBangla == OnBangla.Off;
        }

        void RefreshTray()
        {
            bool paused = Paused;
            tray.Icon = paused ? iconPaused : (avro.Bangla ? iconBangla : iconEnglish);
            string mode = !avro.AvroRunning ? "Avro চলছে না" : (avro.Bangla ? "Avro: বাংলা" : "Avro: English");
            string text = paused ? "LazyReply · বিরতি: " + here.Name + " · চালু করতে ক্লিক"
                : "LazyReply · " + mode + (inline != null && inline.Active ? " · অনুবাদ চালু" : "");
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }

        void ShowSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed)
            {
                settingsForm.Activate();
                return;
            }
            settingsForm = new SettingsForm(settings, avro.AvroKey);
            settingsForm.Icon = iconEnglish;
            settingsForm.Saved += delegate
            {
                avro.PanelKey = KeyCombo.Parse(settings.PanelHotkey) ?? avro.PanelKey;
                avro.OwnKeyCombo = KeyCombo.Parse(settings.OwnSwitchKey);
                RefreshMenu();
                Engine.Warm(settings);
            };
            settingsForm.Show();
            Native.ForceForeground(settingsForm.Handle);
        }

        void Quit()
        {
            Log.Write("quit");
            fgTimer.Stop();
            inline.Deactivate();
            Engine.Shutdown();
            avro.Dispose();
            tray.Visible = false;
            tray.Dispose();
            ExitThread();
        }
    }
}
