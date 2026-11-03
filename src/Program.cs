using System;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace LazyReply
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool first;
            using (var mutex = new Mutex(true, "LazyReply.SingleInstance", out first))
            {
                if (!first)
                {
                    MessageBox.Show("LazyReply আগে থেকেই চলছে। টাস্কবারের ডান কোণে \"অ\" আইকনটি দেখুন।", "LazyReply");
                    return;
                }
                Settings.MoveFromOldName();
                Native.SetProcessDPIAware();
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => Log.Write("UI error: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("fatal: " + e.ExceptionObject);
                Application.Run(new TrayApp());
                GC.KeepAlive(mutex);
            }
        }
    }
}
