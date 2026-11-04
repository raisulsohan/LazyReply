using System;
using System.Collections.Specialized;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LazyReply
{
    // Puts text into the chat box through the clipboard (Ctrl+V works in every app and never
    // presses Enter by itself), then gives the user's own clipboard content back.
    static class Inserter
    {
        class Saved { public string Text; public Image Image; public StringCollection Files; }

        public static void CopyOnly(string text) { SetClip(ClipText(text)); }

        public static void PasteInto(IntPtr hwnd, string text)
        {
            text = ClipText(text);
            Saved saved = Snapshot();
            if (!SetClip(text)) return;
            Native.ForceForeground(hwnd);
            After(90, delegate
            {
                SendCtrlV();
                After(700, delegate { Restore(saved, text); });
            });
        }

        // Replaces everything in the focused text box: Ctrl+A, then Ctrl+V. The keyboard focus does
        // not move. An Alt key the user still holds (from Alt+1) is released first, or the app would
        // see Ctrl+Alt+A. A Ctrl the user holds (Ctrl+Down) stays held. afterPaste runs once the
        // text is in the box.
        public static void ReplaceFocused(string text) { ReplaceFocused(text, null); }

        public static void ReplaceFocused(string text, Action afterPaste)
        {
            text = ClipText(text);
            Saved saved = Snapshot();
            if (!SetClip(text)) return;
            var keys = new System.Collections.Generic.List<Native.INPUT>();
            if (Native.KeyDown(0x12)) keys.Add(Key(0x12, true));
            if (Native.KeyDown(0x10)) keys.Add(Key(0x10, true));
            // Classic Windows edit boxes ignore Ctrl+A; they get the select-all message instead.
            IntPtr focus = Native.FocusedControl();
            string cls = Native.ClassOf(focus);
            bool win32Edit = cls.IndexOf("EDIT", StringComparison.OrdinalIgnoreCase) >= 0;
            if (win32Edit) Native.SendMessage(focus, 0x00B1 /* EM_SETSEL */, IntPtr.Zero, new IntPtr(-1));
            else keys.AddRange(Chord(0x41));
            if (keys.Count > 0) SendKeys(keys.ToArray());
            After(60, delegate
            {
                SendCtrlV();
                if (afterPaste != null) After(150, afterPaste);
                After(700, delegate { Restore(saved, text); });
            });
        }

        // Enter for the chat app, which sends the message. A Ctrl the user holds is released first,
        // so the app sees a plain Enter, not Ctrl+Enter.
        public static void PressEnter()
        {
            var keys = new System.Collections.Generic.List<Native.INPUT>();
            if (Native.KeyDown(0x11)) keys.Add(Key(0x11, true));
            keys.Add(Key(0x0D, false));
            keys.Add(Key(0x0D, true));
            SendKeys(keys.ToArray());
        }

        // Ctrl+key. When the user already holds Ctrl, only the key: sending Ctrl down and up would
        // make Windows think the user had let go of Ctrl, and their next Ctrl+Down would be a plain Down.
        static Native.INPUT[] Chord(ushort vk)
        {
            if (Native.KeyDown(0x11)) return new[] { Key(vk, false), Key(vk, true) };
            return new[] { Key(0x11, false), Key(vk, false), Key(vk, true), Key(0x11, true) };
        }

        static void After(int ms, Action a)
        {
            var t = new Timer { Interval = ms };
            t.Tick += delegate { t.Stop(); t.Dispose(); a(); };
            t.Start();
        }

        // Windows text on the clipboard uses \r\n. Classic edit boxes show a bare \n as nothing, so
        // the lines of a translation would run together there; chat boxes in browsers read \r\n as
        // one line break too.
        static string ClipText(string text) { return Engine.Lines(text).Replace("\n", "\r\n"); }

        static bool SetClip(string text)
        {
            try { Clipboard.SetDataObject(text, true, 8, 60); return true; }
            catch (Exception ex) { Log.Write("clipboard set failed: " + ex.Message); return false; }
        }

        static Saved Snapshot()
        {
            var s = new Saved();
            try
            {
                if (Clipboard.ContainsText()) s.Text = Clipboard.GetText();
                else if (Clipboard.ContainsImage()) s.Image = Clipboard.GetImage();
                else if (Clipboard.ContainsFileDropList()) s.Files = Clipboard.GetFileDropList();
            }
            catch { }
            return s;
        }

        static void Restore(Saved s, string ours)
        {
            try
            {
                if (!Clipboard.ContainsText() || Clipboard.GetText() != ours) return;   // the user copied something new
                if (s.Text != null) Clipboard.SetDataObject(s.Text, true, 8, 60);
                else if (s.Image != null) Clipboard.SetImage(s.Image);
                else if (s.Files != null) Clipboard.SetFileDropList(s.Files);
            }
            catch { }
        }

        static void SendCtrlV()
        {
            SendKeys(Chord(0x56));
        }

        public static void SendKeys(Native.INPUT[] inputs)
        {
            Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        // Every key we send carries our marker, so our own keyboard hook ignores it.
        public static Native.INPUT Key(ushort vk, bool up)
        {
            var i = new Native.INPUT { type = 1 };
            i.u.ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u, dwExtraInfo = AvroWatcher.OwnInputMarker };
            return i;
        }
    }
}
