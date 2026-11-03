using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace LazyReply
{
    static class Native
    {
        public const int WH_KEYBOARD_LL = 13;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_SYSKEYDOWN = 0x0104;
        public const int LLKHF_INJECTED = 0x10;
        public const int WM_MOUSEACTIVATE = 0x21;
        public const int MA_NOACTIVATE = 3;
        public const int WM_NCLBUTTONDOWN = 0xA1;
        public const int HTCAPTION = 2;
        public const int WS_EX_TOPMOST = 0x8;
        public const int WS_EX_TOOLWINDOW = 0x80;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

        public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize;
            public uint flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public InputUnion u; }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);

        // Windows' "Animation effects" switch (Accessibility > Visual effects): off means no gliding.
        public static bool AnimationsOn
        {
            get { bool on; return !SystemParametersInfo(0x1042, 0, out on, 0) || on; }   // SPI_GETCLIENTAREAANIMATION
        }
        [DllImport("oleacc.dll")]
        static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint id, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

        public static bool KeyDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        // The control that has the keyboard focus in the foreground window.
        public static IntPtr FocusedControl()
        {
            uint pid;
            uint tid = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            var gi = new GUITHREADINFO();
            gi.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
            return GetGUIThreadInfo(tid, ref gi) ? gi.hwndFocus : IntPtr.Zero;
        }

        public static string ClassOf(IntPtr hwnd)
        {
            var sb = new System.Text.StringBuilder(256);
            return hwnd != IntPtr.Zero && GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }

        public static uint ProcessOf(IntPtr hwnd)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            return pid;
        }

        // Brings a window to the front even when Windows' focus-stealing rules would refuse it.
        public static void ForceForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
            uint pid;
            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            uint me = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            if (attached) AttachThreadInput(me, fgThread, false);
        }

        // Screen rectangle of the text caret in a window: the Win32 caret first, then the
        // accessibility caret (browsers such as Chrome report it only this way).
        public static Rectangle CaretRect(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return Rectangle.Empty;
            uint pid;
            uint tid = GetWindowThreadProcessId(hwnd, out pid);
            var gi = new GUITHREADINFO();
            gi.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
            IntPtr focus = hwnd;
            if (GetGUIThreadInfo(tid, ref gi))
            {
                if (gi.hwndFocus != IntPtr.Zero) focus = gi.hwndFocus;
                if (gi.hwndCaret != IntPtr.Zero)
                {
                    var p = new POINT { X = gi.rcCaret.Left, Y = gi.rcCaret.Top };
                    ClientToScreen(gi.hwndCaret, ref p);
                    int h = Math.Max(gi.rcCaret.Bottom - gi.rcCaret.Top, 16);
                    if (p.X != 0 || p.Y != 0) return new Rectangle(p.X, p.Y, 2, h);
                }
            }
            try
            {
                Guid iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
                object obj;
                if (AccessibleObjectFromWindow(focus, 0xFFFFFFF8, ref iid, out obj) == 0 && obj != null)
                {
                    var acc = (Accessibility.IAccessible)obj;
                    int l, t, w, h;
                    acc.accLocation(out l, out t, out w, out h, 0);
                    if (l != 0 || t != 0) return new Rectangle(l, t, Math.Max(w, 2), Math.Max(h, 16));
                }
            }
            catch { }
            return Rectangle.Empty;
        }

        public static Rectangle WindowRect(IntPtr hwnd)
        {
            RECT r;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out r)) return Rectangle.Empty;
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        public static void RoundCorners(IntPtr hwnd)
        {
            int pref = 2; // DWMWCP_ROUND
            try { DwmSetWindowAttribute(hwnd, 33, ref pref, 4); } catch { }
        }
    }
}
