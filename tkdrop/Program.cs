using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TkDrop
{
    internal static class Program
    {
        public const string AppName = "tkdrop";

        private static int _wmShowMe;

        /// <summary>Window message broadcast by a second instance to ask the running one to show its window.</summary>
        public static int WM_SHOWME
        {
            get
            {
                if (_wmShowMe == 0)
                {
                    try { _wmShowMe = RegisterWindowMessage("tkdrop.show.{a4f1c0de-7d0b-4c6a-9e2f-3b5d8a1c0f42}"); }
                    catch { _wmShowMe = -1; } // not on Windows (self-test under mono)
                }
                return _wmShowMe;
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            // Load InTheHand.Net.Bluetooth.dll (and anything else embedded by the csproj) from resources.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;
            return Run(args);
        }

        // Kept separate from Main so the JIT does not touch 32feet types before the resolver is registered.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            if (args.Length >= 1 && args[0] == "--selftest")
            {
                return SelfTest.Run(args.Length >= 2 ? args[1] : null);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => ShowFatal(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowFatal(e.ExceptionObject as Exception);

            var settings = AppSettings.Load();
            var items = CollectSendItems(args);

            if (items != null)
            {
                if (items.Count == 0) return 0;
                // "Send mode": launched with file paths (Explorer "Send to", drag onto exe) or --text.
                // Runs independently of the tray instance and exits when the transfer is done.
                using (var form = new SendForm(settings, items, closeWhenDone: true))
                {
                    Application.Run(form);
                }
                return 0;
            }

            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\tkdrop-single-instance", out createdNew))
            {
                if (!createdNew)
                {
                    // Another tray instance exists: ask it to show the send window and quit.
                    PostMessage(HWND_BROADCAST, WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }

                using (var app = new TrayApp(settings))
                {
                    Application.Run(app);
                }
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        /// <summary>Returns the items requested on the command line, or null when the tray should start instead.</summary>
        private static List<SendItem> CollectSendItems(string[] args)
        {
            if (args.Length == 0) return null;

            var items = new List<SendItem>();
            foreach (var a in args)
            {
                if (a == "--text")
                {
                    var text = ClipboardText();
                    if (string.IsNullOrEmpty(text))
                    {
                        MessageBox.Show("클립보드에 보낼 텍스트가 없습니다.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return new List<SendItem>();
                    }
                    items.Add(SendItem.FromText(text));
                }
                else if (File.Exists(a) || Directory.Exists(a))
                {
                    items.AddRange(SendItem.FromPath(a));
                }
            }
            return items;
        }

        public static string ClipboardText()
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch { return null; }
        }

        private static void ShowFatal(Exception ex)
        {
            try
            {
                MessageBox.Show("예기치 않은 오류가 발생했습니다.\n\n" + ex, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        private static Assembly ResolveEmbeddedAssembly(object sender, ResolveEventArgs e)
        {
            var name = new AssemblyName(e.Name).Name;
            if (name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;

            var resource = "tkdrop.embedded." + name + ".dll";
            using (var stream = typeof(Program).Assembly.GetManifestResourceStream(resource))
            {
                if (stream == null) return null;
                var bytes = new byte[stream.Length];
                int read = 0;
                while (read < bytes.Length)
                {
                    int n = stream.Read(bytes, read, bytes.Length - read);
                    if (n <= 0) break;
                    read += n;
                }
                return Assembly.Load(bytes);
            }
        }

        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string message);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
