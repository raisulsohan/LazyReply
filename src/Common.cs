using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace LazyReply
{
    enum OnBangla { Ask = 0, AutoOpen = 1, Off = 2 }

    // Everything the user can change. API keys are stored encrypted with Windows DPAPI,
    // so only this Windows user account can read them.
    class Settings
    {
        public string Provider = "claude-cli";
        public string ClaudeModel = "claude-opus-5-5";
        public string GeminiModel = "";
        public string ClaudeCliModel = "sonnet";
        public string ClaudeKeyEnc = "";
        public string GeminiKeyEnc = "";
        // "openai": any service that speaks OpenAI's Chat Completions API (OpenAI, OpenRouter, Groq,
        // DeepSeek, or a model on this computer through Ollama or LM Studio).
        public string OpenAIBaseUrl = "https://api.openai.com/v1";
        public string OpenAIModel = "";
        public string OpenAIKeyEnc = "";
        public int OnBanglaMode = (int)OnBangla.AutoOpen;
        public string PanelHotkey = "Ctrl+Shift+E";
        public string OwnSwitchKey = "";   // empty: open the box with Avro's own switch key (read from Avro)
        public string AboutMe = "";
        public bool OfferBangla = false;   // English in the chat box also gets a Bangla translation

        public static readonly string[] ClaudeModels = { "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5" };

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LazyReply"); }
        }

        static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

        // [ScriptIgnore] keeps the plain-text keys out of settings.json.
        [ScriptIgnore]
        public OnBangla OnBangla
        {
            get { return (OnBangla)OnBanglaMode; }
            set { OnBanglaMode = (int)value; }
        }

        [ScriptIgnore] public string ClaudeKey { get { return Unprotect(ClaudeKeyEnc); } set { ClaudeKeyEnc = Protect(value); } }
        [ScriptIgnore] public string GeminiKey { get { return Unprotect(GeminiKeyEnc); } set { GeminiKeyEnc = Protect(value); } }
        [ScriptIgnore] public string OpenAIKey { get { return Unprotect(OpenAIKeyEnc); } set { OpenAIKeyEnc = Protect(value); } }
        [ScriptIgnore] public string ActiveKey
        {
            get { return Provider == "gemini" ? GeminiKey : Provider == "openai" ? OpenAIKey : ClaudeKey; }
        }
        // "claude-cli" signs in with the user's Claude account instead of an API key.
        [ScriptIgnore] public bool IsAccount { get { return Provider == "claude-cli"; } }
        // A model on this computer (Ollama, LM Studio) needs no key.
        [ScriptIgnore] public bool NeedsKey { get { return !IsAccount && !(Provider == "openai" && IsLocalUrl(OpenAIBaseUrl)); } }

        public static bool IsLocalUrl(string url)
        {
            Uri u;
            if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out u)) return false;
            return u.IsLoopback || u.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
        }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (s != null)
                    {
                        if (s.Provider == "gemini-cli") s.Provider = "gemini";   // that mode was removed
                        return s;
                    }
                }
            }
            catch (Exception ex) { Log.Write("settings load failed: " + ex.Message); }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this), Encoding.UTF8);
            }
            catch (Exception ex) { Log.Write("settings save failed: " + ex.Message); }
        }

        public Settings Clone() { return (Settings)MemberwiseClone(); }

        // The app was first called BanglaReply. Its settings (API keys included: DPAPI ties them to
        // the Windows user, not to the folder) and its "start with Windows" entry move to the new name.
        public static void MoveFromOldName()
        {
            try
            {
                string old = Path.Combine(Path.GetDirectoryName(Dir), "BanglaReply");
                string oldFile = Path.Combine(old, "settings.json");
                if (!File.Exists(FilePath) && File.Exists(oldFile))
                {
                    Directory.CreateDirectory(Dir);
                    File.Copy(oldFile, FilePath);
                    Log.Write("settings moved from BanglaReply");
                }
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    string here = "\"" + System.Windows.Forms.Application.ExecutablePath + "\"";
                    if (k != null && k.GetValue("BanglaReply") != null)
                    {
                        k.DeleteValue("BanglaReply", false);
                        k.SetValue("LazyReply", here);
                        Log.Write("start-up entry moved from BanglaReply");
                    }
                    // The folder was moved or renamed: "start with Windows" must point at this copy.
                    string entry = k == null ? null : k.GetValue("LazyReply") as string;
                    if (entry != null && !string.Equals(entry, here, StringComparison.OrdinalIgnoreCase))
                    {
                        k.SetValue("LazyReply", here);
                        Log.Write("start-up entry updated to the new folder");
                    }
                }
            }
            catch (Exception ex) { Log.Write("moving old settings failed: " + ex.Message); }
        }

        static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            byte[] b = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain.Trim()), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(b);
        }

        static string Unprotect(string enc)
        {
            if (string.IsNullOrEmpty(enc)) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static bool StartWithWindows
        {
            get
            {
                try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("LazyReply") != null; }
                catch { return false; }
            }
            set
            {
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                    {
                        if (value) k.SetValue("LazyReply", "\"" + System.Windows.Forms.Application.ExecutablePath + "\"");
                        else k.DeleteValue("LazyReply", false);
                    }
                }
                catch (Exception ex) { Log.Write("startup registry failed: " + ex.Message); }
            }
        }
    }

    // Events only, never the text the user types.
    static class Log
    {
        static readonly object Gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Settings.Dir);
                    string path = Path.Combine(Settings.Dir, "log.txt");
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    // A key combination written the way Avro stores it ("CTRL+SPACE") or the way people type it ("Ctrl+Shift+E").
    class KeyCombo
    {
        public bool Ctrl, Alt, Shift, Win;
        public int Vk;

        public static KeyCombo Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var k = new KeyCombo();
            foreach (string raw in text.Split('+'))
            {
                string t = raw.Trim().ToUpperInvariant();
                int n;
                if (t.Length == 0) continue;
                if (t == "CTRL" || t == "CONTROL") k.Ctrl = true;
                else if (t == "ALT") k.Alt = true;
                else if (t == "SHIFT") k.Shift = true;
                else if (t == "WIN") k.Win = true;
                else if (t == "SPACE") k.Vk = 0x20;
                else if (t == "ENTER" || t == "RETURN") k.Vk = 0x0D;
                else if (t == "TAB") k.Vk = 0x09;
                else if (t.Length > 1 && t[0] == 'F' && int.TryParse(t.Substring(1), out n) && n >= 1 && n <= 24) k.Vk = 0x6F + n;
                else if (t.Length == 1 && ((t[0] >= 'A' && t[0] <= 'Z') || (t[0] >= '0' && t[0] <= '9'))) k.Vk = t[0];
                else return null;
            }
            return k.Vk == 0 ? null : k;
        }

        public bool Matches(int vk)
        {
            return vk == Vk
                && Native.KeyDown(0x11) == Ctrl
                && Native.KeyDown(0x12) == Alt
                && Native.KeyDown(0x10) == Shift
                && (Native.KeyDown(0x5B) || Native.KeyDown(0x5C)) == Win;
        }

        public override string ToString()
        {
            var parts = new List<string>();
            if (Ctrl) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            if (Win) parts.Add("Win");
            if (Vk == 0x20) parts.Add("Space");
            else if (Vk == 0x0D) parts.Add("Enter");
            else if (Vk == 0x09) parts.Add("Tab");
            else if (Vk >= 0x70 && Vk <= 0x87) parts.Add("F" + (Vk - 0x6F));
            else parts.Add(((char)Vk).ToString());
            return string.Join("+", parts.ToArray());
        }
    }

    // Colours follow the Windows light/dark app setting.
    static class Theme
    {
        public static bool Dark;
        // Page: window background. Bg: cards and pop-ups. Card: quiet fills inside them (unchosen rows, pills).
        public static Color Page, Bg, Card, Text, Muted, Border, Accent, AccentSoft, OnAccent, Danger, Hover, InputBg, Bangla;
        public static float Sc = 1f;   // screen scale (1 = 100 %)
        public const string FontName = "Nirmala UI";

        public static int S(int v) { return (int)Math.Round(v * Sc); }

        public static void Load()
        {
            Dark = false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    if (v is int && (int)v == 0) Dark = true;
                }
            }
            catch { }
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) Sc = g.DpiX / 96f; } catch { }
            // Soft palette: warm off-white, low-contrast hairlines, a gentle indigo accent.
            if (Dark)
            {
                Page = C(0x1B1A19); Bg = C(0x242322); Card = C(0x2D2B29); Text = C(0xEDEAE5); Muted = C(0x9A958D); Border = C(0x383532);
                Accent = C(0x8B97FF); AccentSoft = C(0x2A2D4A); OnAccent = C(0x11131F); Danger = C(0xF08A80);
                Hover = C(0x302E2C); InputBg = C(0x1F1E1D); Bangla = C(0x4CC38A);
            }
            else
            {
                Page = C(0xFBFAF8); Bg = C(0xFFFFFF); Card = C(0xF6F4F0); Text = C(0x1F1D1A); Muted = C(0x8A857D); Border = C(0xECE9E4);
                Accent = C(0x5B6CF0); AccentSoft = C(0xEEF0FE); OnAccent = Color.White; Danger = C(0xD64545);
                Hover = C(0xF5F3EF); InputBg = Color.White; Bangla = C(0x2BA36B);
            }
        }

        static Color C(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        public static Font F(float pt) { return new Font(FontName, pt, FontStyle.Regular); }
        public static Font FB(float pt) { return new Font(FontName, pt, FontStyle.Bold); }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = Math.Max(1, radius * 2);
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // From a to b: t = 0 gives a, t = 1 gives b.
        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // The same at fractional positions, for shapes that move smoothly between pixels.
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = Math.Max(1f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // The app mark: a rounded square with the letter "অ". Blue = Avro in English, green = Avro in Bangla.
        public static Bitmap Mark(int size, Color bg)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                using (var path = Round(new Rectangle(0, 0, size - 1, size - 1), Math.Max(2, size / 5)))
                using (var b = new SolidBrush(bg))
                    g.FillPath(b, path);
                using (var f = new Font(FontName, size * 0.62f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("অ", f, Brushes.White, new RectangleF(0, size * 0.04f, size, size), sf);
            }
            return bmp;
        }
    }
}
