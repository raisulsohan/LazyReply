using System;
using System.Diagnostics;
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
        readonly ToolStripMenuItem miPause, miInline, miOpen, miAsk, miAuto, miOff;
        SettingsForm settingsForm;
        IntPtr lastExternal;
        // Paused: nothing opens the translation box by itself (Avro's switch key, the user's own key,
        // the tip). A click on the tray icon pauses and resumes. Not saved: every start runs normally.
        bool paused;

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
            miPause = new ToolStripMenuItem("বিরতি   (আইকনে এক ক্লিক)", null, delegate { TogglePause(); });
            miInline = new ToolStripMenuItem("অনুবাদের বাক্স", null, delegate
            {
                if (inline.Active) { inline.Deactivate(); return; }
                SetPaused(false);   // the user asked for the box: that ends a pause
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
                miPause, new ToolStripSeparator(), miInline, miOpen, new ToolStripSeparator(), when,
                new ToolStripMenuItem("সেটিংস…", null, delegate { ShowSettings(); }),
                new ToolStripMenuItem("About LazyReply", null, delegate { AboutForm.ShowOnce(iconEnglish); }),
                new ToolStripSeparator(),
                new ToolStripMenuItem("LazyReply বন্ধ করুন", null, delegate { Quit(); })
            });

            tray = new NotifyIcon { Icon = iconEnglish, Visible = true, ContextMenuStrip = menu };
            // A click pauses LazyReply, or resumes it and opens the translation box in the chat.
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) TogglePause(); };
            RefreshMenu();
            RefreshTray();

            // Remember the last window that was not ours: that is the chat box to paste into.
            fgTimer = new Timer { Interval = 250 };
            fgTimer.Tick += delegate
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg != IntPtr.Zero && Native.ProcessOf(fg) != myPid && !IsTaskbar(fg)) lastExternal = fg;
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

        string HotkeyText { get { return avro.PanelKey == null ? "" : avro.PanelKey.ToString(); } }

        void OnModeChanged(bool bangla, bool fromSwitchKey)
        {
            RefreshTray();
            if (paused || panel.IsActiveWindow) return;
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
        void OnBoxKey()
        {
            if (paused) return;
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

        void TogglePause()
        {
            if (!paused)
            {
                SetPaused(true);
                tray.ShowBalloonTip(2500, "LazyReply বিরতিতে", "এখন কোনো অনুবাদ আসবে না। আবার চালু করতে আইকনে একবার ক্লিক করুন।", ToolTipIcon.Info);
                return;
            }
            SetPaused(false);
            // The tray click made the taskbar the active window: give the chat window its focus back.
            if (lastExternal != IntPtr.Zero && Native.IsWindow(lastExternal)) Native.ForceForeground(lastExternal);
            inline.Open();
            tray.ShowBalloonTip(3500, "অনুবাদ চালু", "চ্যাটের বক্সে বাংলা বা Banglish লিখুন। দাঁড়ি (।) বা ? দিলে বক্সের উপরে ইংরেজি আসবে।", ToolTipIcon.Info);
        }

        void SetPaused(bool value)
        {
            if (paused == value) return;
            paused = value;
            Log.Write(value ? "paused" : "resumed");
            if (value) { toast.HideToast(); inline.Deactivate(); }
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
            miPause.Checked = paused;
            miInline.Checked = inline.Active;
            miOpen.Text = "আলাদা অনুবাদক উইন্ডো" + (HotkeyText.Length > 0 ? "   (" + HotkeyText + ")" : "");
            miAsk.Checked = settings.OnBangla == OnBangla.Ask;
            miAuto.Checked = settings.OnBangla == OnBangla.AutoOpen;
            miOff.Checked = settings.OnBangla == OnBangla.Off;
        }

        void RefreshTray()
        {
            tray.Icon = paused ? iconPaused : (avro.Bangla ? iconBangla : iconEnglish);
            string mode = !avro.AvroRunning ? "Avro চলছে না" : (avro.Bangla ? "Avro: বাংলা" : "Avro: English");
            string text = paused ? "LazyReply · বিরতি · চালু করতে ক্লিক করুন"
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
