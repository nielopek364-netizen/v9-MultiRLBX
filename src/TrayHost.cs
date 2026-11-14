using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace MultiRLBX
{
    /// <summary>
    /// Lightweight Windows message-pump host running in the system tray. Manages the
    /// <see cref="NotifyIcon"/> menu, owns the hidden WinForms marshal control for tool windows,
    /// starts <see cref="LocalUiServer"/> and <see cref="AppEngine"/>, and listens for single-instance wake signals.
    /// </summary>
    public class TrayHost : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly Control invoker;
        readonly ToolStripMenuItem miWatchdog;
        readonly EventWaitHandle wakeEvent;
        volatile bool stopping;

        /// <summary>Boots the local UI server, core engine, tray menu, and optional desktop shell.</summary>
        /// <param name="minimized">When <c>true</c>, starts silently in the tray without opening the desktop window.</param>
        public TrayHost(bool minimized)
        {
            invoker = new Control();
            invoker.CreateControl();

            LocalUiServer.Start();
            AppEngine.Init(invoker);

            tray = new NotifyIcon();
            tray.Text = "v9 MultiRLBX v" + Updater.CurrentVersion() + " · v9pv";
            try { tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (Exception) { tray.Icon = SystemIcons.Application; }
            tray.Visible = true;
            tray.DoubleClick += delegate { DesktopShell.Reopen(); };
            tray.BalloonTipClicked += delegate { DesktopShell.Reopen(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(Lang.T("Open Hub"), null, delegate { DesktopShell.Reopen(); });
            menu.Items.Add(Lang.T("Open in browser"), null, delegate { DesktopShell.OpenInDefaultBrowser(LocalUiServer.LocalUrl); });
            menu.Items.Add(Lang.T("Copy web URL"), null, delegate
            {
                try
                {
                    Clipboard.SetText(LocalUiServer.LocalUrl);
                    tray.ShowBalloonTip(3000, "v9 MultiRLBX", Lang.T("Copied web URL") + "\n" + LocalUiServer.LocalUrl, ToolTipIcon.Info);
                }
                catch (Exception) { }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Lang.T("Launch all"), null, async delegate { await AppEngine.LaunchAccountsAsync(Store.Accounts, null); });
            menu.Items.Add(Lang.T("Stop all"), null, delegate { AppEngine.StopAll(); });
            menu.Items.Add(Lang.T("Clean RAM"), null, async delegate { await AppEngine.CleanMemoryAsync(); });
            menu.Items.Add(Lang.T("Macro Recorder (.v9m)"), null, delegate { AppEngine.RunOnUi(delegate { MacroForm.ShowOrFocus(); }); });
            miWatchdog = new ToolStripMenuItem(Store.Settings.AutoRelaunch ? Lang.T("Watchdog: ON") : Lang.T("Watchdog: OFF"));
            miWatchdog.Click += delegate
            {
                Store.Settings.AutoRelaunch = !Store.Settings.AutoRelaunch;
                Store.SaveSettings();
                miWatchdog.Text = Store.Settings.AutoRelaunch ? Lang.T("Watchdog: ON") : Lang.T("Watchdog: OFF");
            };
            menu.Items.Add(miWatchdog);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Lang.T("Exit"), null, delegate { ExitApp(); });
            tray.ContextMenuStrip = menu;

            bool created;
            wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "MultiRLBX_Wake_v1", out created);
            Thread wakeThread = new Thread(WaitForWake);
            wakeThread.IsBackground = true;
            wakeThread.Start();

            tray.ShowBalloonTip(5000, "v9 MultiRLBX", LocalUiServer.LocalUrl, ToolTipIcon.Info);
            if (!minimized) DesktopShell.Open(LocalUiServer.LocalUrl);
        }

        void WaitForWake()
        {
            while (!stopping)
            {
                try
                {
                    if (wakeEvent.WaitOne(1000) && !stopping)
                        AppEngine.RunOnUi(delegate { DesktopShell.Reopen(); });
                }
                catch (Exception) { break; }
            }
        }

        void ExitApp()
        {
            stopping = true;
            try { wakeEvent.Set(); } catch (Exception) { }
            try { tray.Visible = false; tray.Dispose(); } catch (Exception) { }
            AppEngine.Shutdown();
            ExitThread();
        }
    }
}
