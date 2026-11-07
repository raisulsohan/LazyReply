using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace LazyReply
{
    // Where the user is typing: an app ("app:claude") or, in a web browser, a website
    // ("web:facebook.com"). A pause belongs to one such place, so a pause in the Claude app
    // leaves Facebook in the browser alone, and one site's pause leaves the other sites alone.
    class Place
    {
        public string Key;    // "app:<process>" or "web:<host>"
        public string Name;   // what the user sees: "Claude", "facebook.com"

        public static string NameOf(string key)
        {
            if (key == null) return "";
            if (key.StartsWith("web:")) return key.Substring(4);
            string p = key.StartsWith("app:") ? key.Substring(4) : key;
            return p.Length == 0 ? p : char.ToUpperInvariant(p[0]) + p.Substring(1);
        }
    }

    static class Places
    {
        static readonly string[] Browsers =
        {
            "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "arc", "thorium", "chromium",
            "librewolf", "waterfox", "floorp", "zen", "browser" /* Yandex */, "iexplore"
        };

        static volatile bool busy;
        static readonly object gate = new object();
        // Per window: its app, its address bar (found once, then only read) and the last site seen.
        static readonly Dictionary<IntPtr, string> appOf = new Dictionary<IntPtr, string>();
        static readonly Dictionary<IntPtr, AutomationElement> barOf = new Dictionary<IntPtr, AutomationElement>();
        static readonly Dictionary<IntPtr, string> siteOf = new Dictionary<IntPtr, string>();

        public static bool IsBrowser(string app) { return Array.IndexOf(Browsers, app) >= 0; }

        // The place of a window. UI Automation can hang on a busy app, so it runs on a worker thread
        // with a time limit. Null: not known now (the caller keeps what it knew before).
        public static async Task<Place> OfAsync(IntPtr hwnd, int timeoutMs)
        {
            if (hwnd == IntPtr.Zero || busy) return null;
            busy = true;
            var work = Task.Run(delegate
            {
                try { return Of(hwnd); }
                catch (Exception ex) { Log.Write("place read failed: " + ex.GetType().Name); return null; }
                finally { busy = false; }
            });
            if (await Task.WhenAny(work, Task.Delay(timeoutMs)) == work) return work.Result;
            Log.Write("place read timed out");
            return null;
        }

        static Place Of(IntPtr hwnd)
        {
            string app = AppOf(hwnd);
            if (app == null) return null;
            if (!IsBrowser(app)) return Make("app:" + app);

            string site = SiteOf(hwnd);
            lock (gate)
            {
                if (site != null) siteOf[hwnd] = site;
                else siteOf.TryGetValue(hwnd, out site);   // the user is typing an address, or a new tab: keep the last site
            }
            return Make(site != null ? "web:" + site : "app:" + app);
        }

        static Place Make(string key) { return new Place { Key = key, Name = Place.NameOf(key) }; }

        static string AppOf(IntPtr hwnd)
        {
            lock (gate)
            {
                string app;
                if (appOf.TryGetValue(hwnd, out app)) return app;
            }
            uint pid = Native.ProcessOf(hwnd);
            if (pid == 0) return null;
            string name;
            try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName.ToLowerInvariant(); }
            catch (Exception) { return null; }   // the process ended
            lock (gate)
            {
                if (appOf.Count > 200) { appOf.Clear(); barOf.Clear(); siteOf.Clear(); }
                appOf[hwnd] = name;
            }
            return name;
        }

        // The website in the browser window's address bar: its host without "www." or "m.".
        static string SiteOf(IntPtr hwnd)
        {
            AutomationElement bar;
            lock (gate) barOf.TryGetValue(hwnd, out bar);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (bar == null)
                {
                    AutomationElement win = AutomationElement.FromHandle(hwnd);
                    if (win == null) return null;
                    // The address bar is the first text box in the browser's own toolbar, before the page.
                    bar = win.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                    if (bar == null) return null;
                    lock (gate) barOf[hwnd] = bar;
                }
                try
                {
                    if (bar.Current.HasKeyboardFocus) return null;   // the user is typing an address
                    object pat;
                    if (!bar.TryGetCurrentPattern(ValuePattern.Pattern, out pat)) return null;
                    return HostOf(((ValuePattern)pat).Current.Value);
                }
                catch (ElementNotAvailableException)
                {
                    bar = null;   // the toolbar was rebuilt: find the address bar again
                    lock (gate) barOf.Remove(hwnd);
                }
            }
            return null;
        }

        public static string HostOf(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || text.IndexOf(' ') >= 0) return null;
            if (text.IndexOf("://", StringComparison.Ordinal) < 0) text = "https://" + text;
            Uri u;
            if (!Uri.TryCreate(text, UriKind.Absolute, out u)) return null;
            if (u.IsFile) return "file";
            string h = u.Host.ToLowerInvariant();
            if (h.StartsWith("www.")) h = h.Substring(4);
            else if (h.StartsWith("m.")) h = h.Substring(2);
            return h.Length == 0 ? null : h;
        }
    }
}
