using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace LazyReply
{
    // Follows Avro Keyboard's English/Bangla mode without changing Avro.
    //
    // Avro keeps no readable mode flag, so the mode is followed two ways:
    //  1. Avro's own switch key (read from Avro's registry settings, usually Ctrl+Space) flips it.
    //  2. Proof from typing: in Bangla mode Avro answers every letter key with injected Unicode
    //     keys (VK_PACKET) or backspaces. A letter with an echo means Bangla; once echoes have been
    //     seen at least once, a letter with no echo means English. This corrects any drift, for
    //     example when the mode was changed with the mouse.
    //
    // The keyboard hook only watches; it passes every key on, except our own panel shortcut.
    class AvroWatcher : IDisposable
    {
        public event Action<bool, bool> ModeChanged;   // (bangla, fromSwitchKey)
        public event Action PanelHotkey;
        public event Action<bool> Typed;               // the text in the focused box may have changed (true: a sentence ended)
        public event Action<int> AltKey;               // Alt+1..4 -> 1..4, Alt+Y -> 0
        public event Action<int> CtrlArrow;            // Ctrl+Down -> +1, Ctrl+Up -> -1
        public event Action CtrlEnter;                 // send the chosen version
        public event Action OwnKey;                    // the user's own key for the translation box

        public bool Bangla { get; private set; }
        public KeyCombo AvroKey { get; private set; }
        public KeyCombo PanelKey;
        public KeyCombo OwnKeyCombo;                   // null: Avro's switch key opens the box
        public bool CaptureAltDigits, CaptureAltY, CaptureCtrlArrows;   // taken from the app only while our pop-ups need them
        public bool AcceptInjectedSwitch;              // only for automated tests

        public static readonly IntPtr OwnInputMarker = new IntPtr(0x42524C59);
        const int EchoWindowMs = 350;

        readonly Native.LowLevelKeyboardProc proc;
        readonly SynchronizationContext ui;
        readonly System.Windows.Forms.Timer timer;
        IntPtr hook = IntPtr.Zero;
        bool awaitingEcho, echoSeenEver;
        int echoTick, ticks, avroPid = -2, keysSeen, packetsSeen;

        public AvroWatcher()
        {
            ui = SynchronizationContext.Current;
            proc = HookProc;
            ReadAvroKey();
            InstallHook();
            CheckAvroProcess();
            timer = new System.Windows.Forms.Timer { Interval = 150 };
            timer.Tick += OnTick;
            timer.Start();
        }

        public bool AvroRunning { get { return avroPid > 0; } }

        public void ReadAvroKey()
        {
            string v = null;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\OmicronLab\Avro Keyboard"))
                    if (k != null) v = k.GetValue("ModeSwitchKey") as string;
            }
            catch { }
            AvroKey = KeyCombo.Parse(v) ?? KeyCombo.Parse("Ctrl+Space");
        }

        // A newer low-level hook runs before older ones, so the hook is installed again whenever
        // Avro restarts: Avro must not see (and swallow) its switch key before we do.
        void InstallHook()
        {
            IntPtr h = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
            if (h == IntPtr.Zero)
            {
                Log.Write("keyboard hook failed, error " + Marshal.GetLastWin32Error());
                return;
            }
            IntPtr old = hook;
            hook = h;
            if (old != IntPtr.Zero) Native.UnhookWindowsHookEx(old);
        }

        void CheckAvroProcess()
        {
            int pid = -1;
            try
            {
                Process[] list = Process.GetProcessesByName("Avro Keyboard");
                if (list.Length > 0) pid = list[0].Id;
                foreach (var p in list) p.Dispose();
            }
            catch { }
            if (pid == avroPid) return;
            bool first = avroPid == -2;
            avroPid = pid;
            Log.Write(pid > 0 ? "Avro running, pid " + pid + ", switch key " + AvroKey : "Avro not running");
            if (!first && pid > 0)
            {
                ReadAvroKey();
                InstallHook();
                SetMode(false, false, "Avro restarted");
            }
        }

        void OnTick(object sender, EventArgs e)
        {
            if (awaitingEcho && unchecked(Environment.TickCount - echoTick) > EchoWindowMs)
            {
                awaitingEcho = false;
                if (echoSeenEver && Bangla) SetMode(false, false, "letter without echo");
            }
            ticks++;
            if (ticks % 400 == 0 && keysSeen > 0)                       // about every minute: counts only, never keys
            {
                Log.Write("keyboard: " + keysSeen + " keys, " + packetsSeen + " from Avro");
                keysSeen = packetsSeen = 0;
            }
            if (ticks % 20 == 0) { ReadAvroKey(); CheckAvroProcess(); }  // about every 3 s
            // About every 10 s: Avro hooks the keyboard late (after its splash screen), and the newest
            // hook sees keys first. Staying newest lets us see Avro's switch key before Avro takes it.
            if (ticks % 66 == 0) InstallHook();
        }

        IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                try
                {
                    int msg = wParam.ToInt32();
                    if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
                    {
                        var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                        int vk = (int)k.vkCode;
                        bool injected = (k.flags & Native.LLKHF_INJECTED) != 0;
                        bool ours = k.dwExtraInfo == OwnInputMarker;
                        if (!ours) { if (injected && vk == 0xE7) packetsSeen++; else if (!injected) keysSeen++; }

                        if (injected && !ours && awaitingEcho && (vk == 0xE7 || vk == 0x08))
                        {
                            awaitingEcho = false;
                            echoSeenEver = true;
                            if (!Bangla) Post(delegate { SetMode(true, false, "letter echo"); });
                        }
                        else if (injected && !ours && vk == 0xE7 && avroPid > 0)
                        {
                            // Avro's hook ran before ours and took the letter itself; its Bangla output still arrives.
                            echoSeenEver = true;
                            if (!Bangla) Post(delegate { SetMode(true, false, "Avro output"); });
                        }

                        if (!ours && !IsModifier(vk))
                        {
                            bool ctrl = Native.KeyDown(0x11), alt = Native.KeyDown(0x12), win = Native.KeyDown(0x5B) || Native.KeyDown(0x5C);
                            if (alt && !ctrl && !win)
                            {
                                int n = vk >= 0x31 && vk <= 0x34 ? vk - 0x30 : (vk >= 0x61 && vk <= 0x64 ? vk - 0x60 : -1);
                                if ((CaptureAltDigits && n > 0) || (CaptureAltY && vk == 0x59))
                                {
                                    MaskAlt();
                                    int which = n > 0 ? n : 0;
                                    Post(delegate { if (AltKey != null) AltKey(which); });
                                    return new IntPtr(1);
                                }
                            }
                            if (CaptureCtrlArrows && ctrl && !alt && !win && !Native.KeyDown(0x10) && (vk == 0x26 || vk == 0x28))
                            {
                                int dir = vk == 0x28 ? 1 : -1;
                                Post(delegate { if (CtrlArrow != null) CtrlArrow(dir); });
                                return new IntPtr(1);
                            }
                            if (CaptureCtrlArrows && ctrl && !alt && !win && !Native.KeyDown(0x10) && vk == 0x0D)
                            {
                                Post(delegate { if (CtrlEnter != null) CtrlEnter(); });
                                return new IntPtr(1);
                            }
                            // Avro types Bangla as injected Unicode keys (VK_PACKET); those count too.
                            if (((!injected || AcceptInjectedSwitch) && ChangesText(vk, ctrl, alt)) || (injected && vk == 0xE7))
                            {
                                bool end = EndsSentence(vk, injected, (char)k.scanCode, ctrl, alt);
                                Post(delegate { if (Typed != null) Typed(end); });
                            }
                        }

                        if (!ours && !IsModifier(vk))
                        {
                            if (PanelKey != null && PanelKey.Matches(vk))
                            {
                                Post(delegate { if (PanelHotkey != null) PanelHotkey(); });
                                return new IntPtr(1);
                            }
                            // The own key is passed on to the app too: it may be the user's keyboard switch (Win+Space).
                            if ((!injected || AcceptInjectedSwitch) && OwnKeyCombo != null && OwnKeyCombo.Matches(vk))
                                Post(delegate { if (OwnKey != null) OwnKey(); });
                            if ((!injected || AcceptInjectedSwitch) && AvroKey != null && AvroKey.Matches(vk))
                            {
                                awaitingEcho = false;
                                Post(delegate { SetMode(!Bangla, true, "switch key"); });
                            }
                            else if (!injected && vk >= 0x41 && vk <= 0x5A
                                     && !Native.KeyDown(0x11) && !Native.KeyDown(0x12) && !Native.KeyDown(0x5B) && !Native.KeyDown(0x5C))
                            {
                                awaitingEcho = true;
                                echoTick = Environment.TickCount;
                            }
                        }
                    }
                }
                catch { }
            }
            return Native.CallNextHookEx(hook, nCode, wParam, lParam);
        }

        bool lastWasDigit, lastEnded;

        // A key that ends a sentence: the Bangla dari "।", "?", "!", or "." (the dari of English and Banglish).
        // A "." right after a digit is a decimal point ("2.5"), not the end of a sentence.
        // A space right after the end keeps it ended: "Hello. " is a finished sentence too, and the
        // space often comes before the request would go.
        bool EndsSentence(int vk, bool injected, char typed, bool ctrl, bool alt)
        {
            bool space = vk == 0xE7 ? typed == ' ' : (vk == 0x20 && !ctrl && !alt);
            if (space) { lastWasDigit = false; return lastEnded; }
            bool end, digit;
            if (vk == 0xE7)   // Avro's output carries the character itself
            {
                end = typed == '।' || typed == '?' || typed == '!' || (typed == '.' && !lastWasDigit);
                digit = (typed >= '0' && typed <= '9') || (typed >= '০' && typed <= '৯');
            }
            else
            {
                bool shift = Native.KeyDown(0x10);
                end = !ctrl && !alt && ((vk == 0xBF && shift) || (vk == 0x31 && shift) || (vk == 0xBE && !shift && !lastWasDigit));
                digit = !shift && ((vk >= 0x30 && vk <= 0x39) || (vk >= 0x60 && vk <= 0x69));
            }
            lastWasDigit = digit;
            lastEnded = end;
            return end;
        }

        static bool ChangesText(int vk, bool ctrl, bool alt)
        {
            if (ctrl) return vk == 0x56 || vk == 0x58 || vk == 0x5A || vk == 0x59;   // paste, cut, undo, redo
            if (alt) return false;
            return vk == 0x08 || vk == 0x0D || vk == 0x20 || vk == 0x2E || vk == 0xE7
                || (vk >= 0x30 && vk <= 0x5A) || (vk >= 0x60 && vk <= 0x6F) || (vk >= 0xBA && vk <= 0xC0) || (vk >= 0xDB && vk <= 0xDF);
        }

        // When we swallow Alt+key, the app sees Alt go down and up with nothing between, and
        // many apps (browsers too) then open their menu. A dummy key in between prevents that.
        static void MaskAlt()
        {
            Inserter.SendKeys(new[] { Inserter.Key(0xE8, false), Inserter.Key(0xE8, true) });
        }

        static bool IsModifier(int vk)
        {
            return vk == 0x10 || vk == 0x11 || vk == 0x12 || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C;
        }

        // The hook must return fast, so all real work runs later on the UI thread.
        void Post(Action a)
        {
            if (ui != null) ui.Post(delegate { a(); }, null);
            else a();
        }

        void SetMode(bool bangla, bool fromSwitchKey, string why)
        {
            if (bangla == Bangla && !fromSwitchKey) return;
            Bangla = bangla;
            Log.Write("Avro mode -> " + (bangla ? "Bangla" : "English") + " (" + why + ")");
            if (ModeChanged != null) ModeChanged(bangla, fromSwitchKey);
        }

        public void Dispose()
        {
            timer.Stop();
            if (hook != IntPtr.Zero) Native.UnhookWindowsHookEx(hook);
            hook = IntPtr.Zero;
        }
    }
}
