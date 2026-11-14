using System;
using System.Threading;
using System.Windows.Forms;

namespace MultiRLBX
{
    /// <summary>
    /// Application entry point handling CLI sub-modes (<c>--memhelper</c>, <c>--memclean</c>,
    /// <c>--minimized</c>), single-instance coordination, and vault unlock.
    /// </summary>
    static class Program
    {
        static Mutex single;

        /// <summary>Main STA entry point.</summary>
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { OnCrash(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { OnCrash(e.ExceptionObject as Exception); };

            int helper = Array.IndexOf(args, "--memhelper");
            if (helper >= 0 || Array.IndexOf(args, "--memclean") >= 0)
            {
                try
                {
                    int dd = Array.IndexOf(args, "--datadir");
                    Store.Init(dd >= 0 && dd + 1 < args.Length ? args[dd + 1] : null);
                    int parent;
                    if (helper >= 0 && helper + 1 < args.Length && int.TryParse(args[helper + 1], out parent)) MemClean.RunHelper(parent);
                    else if (helper < 0) MemClean.RunOnce();
                }
                catch (Exception ex) { Log.Error("memory cleaner", ex); }
                return;
            }

            bool created;
            single = new Mutex(true, "MultiRLBX_SingleInstance_v1", out created);
            if (!created)
            {
                try
                {
                    EventWaitHandle ev = EventWaitHandle.OpenExisting("MultiRLBX_Wake_v1");
                    ev.Set();
                    ev.Close();
                }
                catch (Exception)
                {
                    MessageBox.Show("v9 MultiRLBX jest juz uruchomiony. Sprawdz zasobnik systemowy (tray).", "v9 MultiRLBX", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }

            Store.Init();
            Lang.Init();
            Theme.Set(Store.Settings.Theme != "Light");
            Theme.SetAccent(Store.Settings.Accent);
            Theme.LoadOverrides();

            if (!Unlock()) return;

            bool minimized = Array.IndexOf(args, "--minimized") >= 0;
            Application.Run(new TrayHost(minimized));
            MultiInstance.Stop();
            GC.KeepAlive(single);
        }

        static bool Unlock()
        {
            try
            {
                if (Store.AccountsFileIsPasswordProtected())
                {
                    while (true)
                    {
                        string pw = Dialogs.AskPassword(null, "v9 MultiRLBX - odblokuj / unlock", "Podaj haslo do pliku kont / Enter accounts password:", false);
                        if (pw == null) return false;
                        string err = Store.LoadAccounts(pw);
                        if (err == null) return true;
                        MessageBox.Show(err, "v9 MultiRLBX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                string problem = Store.LoadAccounts(null);
                if (problem == null) return true;
                DialogResult r = MessageBox.Show(problem + "\r\n\r\nTAK / YES = uruchom z pusta lista (stary plik pozostaje jako accounts.dat.old)\r\nNIE / NO = zamknij",
                    "v9 MultiRLBX", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.Yes) { Store.StartFresh(); return true; }
                return false;
            }
            catch (Exception ex)
            {
                OnCrash(ex);
                return false;
            }
        }

        static void OnCrash(Exception ex)
        {
            try
            {
                if (ex != null) Log.Error("UNHANDLED", ex);
                MessageBox.Show("Wystapil nieoczekiwany blad:\r\n\r\n" + (ex == null ? "nieznany blad" : ex.Message)
                    + "\r\n\r\nSzczegoly zapisano w log.txt.",
                    "v9 MultiRLBX", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception) { }
        }
    }
}
