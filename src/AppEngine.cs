using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MultiRLBX
{
    /// <summary>Represents a timestamped runtime log line surfaced in the Web UI console.</summary>
    public class LogEntry
    {
        public string Time = "";
        public string Message = "";
    }

    /// <summary>
    /// Core non-UI application orchestrator managing launch queues, Watchdog/Ghost Killer cycles,
    /// Smart Eco (<see cref="MaxAlts"/>), elevated RAM cleaner coordination, Anti-AFK ticks, and tool windows.
    /// </summary>
    public static class AppEngine
    {
        static readonly object logLock = new object();
        static readonly List<LogEntry> logs = new List<LogEntry>();
        static CancellationTokenSource launchCts;
        static System.Windows.Forms.Timer fastTimer;
        static System.Windows.Forms.Timer slowTimer;
        static Control uiInvoker;

        static bool fastBusy;
        static bool slowBusy;
        static bool memBusy;
        static long memSeen;
        static DateTime lastCookieWatch = DateTime.MinValue;
        static DateTime lastAntiAfk = DateTime.UtcNow;
        static DateTime lastScheduledRestart = DateTime.UtcNow;
        static DateTime lastActivity = DateTime.UtcNow;
        static string lastDailySentDate = "";
        static string lastSchedStartDate = "";
        static string lastSchedStopDate = "";

        public static volatile bool LaunchBusy;
        public static volatile int QueueTotal;
        public static volatile int QueueDone;
        public static volatile string QueueCurrent = "";
        public static volatile bool IsLocked;
        public static DateTime SessionStartedUtc = DateTime.UtcNow;
        public static int DailyCleans;
        public static int TotalLaunches;

        public static readonly MacroPlayer WebMacroPlayer = new MacroPlayer();

        /// <summary>Initializes background timers, Smart Eco/Multi-Instance hooks, and startup batch/cookie tasks.</summary>
        /// <param name="invoker">Hidden WinForms control used to marshal tool-window creation onto the STA UI thread.</param>
        public static void Init(Control invoker)
        {
            uiInvoker = invoker;
            SessionStartedUtc = DateTime.UtcNow;
            if (Store.Settings.MultiInstance) MultiInstance.Start();
            if (Store.Settings.Mem.AutoClean) StartMemHelper();
            Hotkeys.SetEnabled(Store.Settings.GlobalHotkeys);
            Gfx.ApplyPreset(Store.Settings.Gfx, Store.Settings.Gfx.Preset);

            fastTimer = new System.Windows.Forms.Timer();
            fastTimer.Interval = 2000;
            fastTimer.Tick += delegate { OnFastTick(); };
            fastTimer.Start();

            slowTimer = new System.Windows.Forms.Timer();
            slowTimer.Interval = 15000;
            slowTimer.Tick += delegate { OnSlowTick(); };
            slowTimer.Start();

            UsageMonitor.Update();
            AddLog("v9 MultiRLBX " + Updater.CurrentVersion() + " uruchomiony / started.");
            if (Store.MigratedFromMultiHub) AddLog("Zaimportowano ustawienia i konta z poprzedniej wersji.");

            Task.Run(async delegate
            {
                if (Store.Settings.CheckLoginsAtStartup && Store.Accounts.Count > 0)
                {
                    await CheckAllCookiesAsync();
                }
                if (!string.IsNullOrEmpty(Store.Settings.StartupGroup))
                {
                    List<Account> grp = Store.Accounts.Where(x => (x.Group ?? "").Equals(Store.Settings.StartupGroup, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (grp.Count > 0) await LaunchAccountsAsync(grp, null);
                }
            });
        }

        /// <summary>Stops background timers, resets CPU/power throttling, and tears down listeners.</summary>
        public static void Shutdown()
        {
            try { if (fastTimer != null) fastTimer.Stop(); } catch (Exception) { }
            try { if (slowTimer != null) slowTimer.Stop(); } catch (Exception) { }
            try { MaxAlts.Reset(); } catch (Exception) { }
            try { Hotkeys.Dispose(); } catch (Exception) { }
            try { MultiInstance.Stop(); } catch (Exception) { }
            try { WebAuth.Cancel(); } catch (Exception) { }
            try { LocalUiServer.Stop(); } catch (Exception) { }
        }

        public static void RunOnUi(Action action)
        {
            if (action == null) return;
            if (uiInvoker != null && uiInvoker.InvokeRequired)
            {
                try { uiInvoker.BeginInvoke(action); } catch (Exception) { }
            }
            else
            {
                action();
            }
        }

        public static void TouchActivity()
        {
            lastActivity = DateTime.UtcNow;
        }

        public static void AddLog(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Log.Write(text);
            lock (logLock)
            {
                LogEntry e = new LogEntry();
                e.Time = DateTime.Now.ToString("HH:mm:ss");
                e.Message = text;
                logs.Add(e);
                if (logs.Count > 250) logs.RemoveRange(0, logs.Count - 250);
            }
        }

        public static List<LogEntry> GetLogs()
        {
            lock (logLock) { return logs.ToList(); }
        }

        static void OnFastTick()
        {
            if (fastBusy) return;
            fastBusy = true;
            try
            {
                UsageMonitor.Update();
                MaxAlts.Tick();
                CheckMemResult();

                if (Store.Settings.LockIdleMin > 0 && !IsLocked && Store.Password != null)
                {
                    if ((DateTime.UtcNow - lastActivity).TotalMinutes >= Store.Settings.LockIdleMin)
                    {
                        IsLocked = true;
                        AddLog("Auto-blokada aktywowana po bezczynnosci.");
                    }
                }
            }
            catch (Exception ex) { Log.Error("AppEngine.FastTick", ex); }
            finally { fastBusy = false; }
        }

        static async void OnSlowTick()
        {
            if (slowBusy) return;
            slowBusy = true;
            try
            {
                await Launcher.WatcherTick(AddLog);

                if (Store.Settings.AntiAfk && !MacroRecorder.Recording && !WebMacroPlayer.Playing)
                {
                    int mins = Math.Max(1, Store.Settings.AntiAfkMin);
                    if ((DateTime.UtcNow - lastAntiAfk).TotalMinutes >= mins)
                    {
                        lastAntiAfk = DateTime.UtcNow;
                        int poked = await AntiAfk.Pulse(AddLog);
                        if (poked > 0) AddLog("Anti-AFK: odswiezono " + poked + " okien.");
                    }
                }

                if (Store.Settings.CookieWatch && Store.Accounts.Count > 0)
                {
                    int hours = Math.Max(1, Store.Settings.CookieWatchHours);
                    if ((DateTime.UtcNow - lastCookieWatch).TotalHours >= hours)
                    {
                        lastCookieWatch = DateTime.UtcNow;
                        await CheckAllCookiesAsync();
                    }
                }

                if (Store.Settings.DailyReport && Store.Settings.Webhook)
                {
                    string today = DateTime.Now.ToString("yyyy-MM-dd");
                    string hm = DateTime.Now.ToString("HH:mm");
                    if (lastDailySentDate != today && hm == (Store.Settings.DailyTime ?? "20:00"))
                    {
                        lastDailySentDate = today;
                        int load; long total, avail;
                        string ramStr = MemClean.Status(out load, out total, out avail) ? Util.FormatBytes(avail) : "n/a";
                        TimeSpan up = DateTime.UtcNow - SessionStartedUtc;
                        Webhook.SendStatusFields("Raport dzienny / Daily report", "Podsumowanie sesji MultiRLBX.", Webhook.Blurple,
                            new string[] {
                                "Okna / Windows", UsageMonitor.Count.ToString(),
                                "Wolny RAM / Free RAM", ramStr,
                                "Relaunch", Launcher.DailyRelaunches.ToString(),
                                "RAM Cleans", DailyCleans.ToString(),
                                "Uptime", string.Format("{0}h {1}m", (int)up.TotalHours, up.Minutes)
                            });
                        Launcher.DailyRelaunches = 0;
                        DailyCleans = 0;
                    }
                }

                await RunSchedulerTickAsync();
            }
            catch (Exception ex) { Log.Error("AppEngine.SlowTick", ex); }
            finally { slowBusy = false; }
        }

        /// <summary>Batch-validates every stored account cookie and dispatches lock/expiry webhook alerts.</summary>
        public static async Task CheckAllCookiesAsync()
        {
            List<Account> copy = Store.Accounts.ToList();
            if (copy.Count == 0) return;
            Dictionary<string, UserInfo> res = await Api.CheckCookiesBatch(copy, 4);
            int bad = 0;
            int locked = 0;
            foreach (Account a in copy)
            {
                UserInfo u;
                if (!res.TryGetValue(a.Id, out u)) continue;
                LiveInfo li = AppState.Live(a);
                li.CookieBad = !u.Valid;
                li.Locked = u.Locked;
                if (u.Locked)
                {
                    locked++;
                    if (!li.LockedAlerted && Store.Settings.WebhookLocked)
                    {
                        li.LockedAlerted = true;
                        Webhook.Send("Konto zablokowane (HTTP 403)", AppState.Name(a) + " wymaga odblokowania na roblox.com.", Webhook.Red);
                    }
                }
                else if (!u.Valid)
                {
                    bad++;
                    if (!a.LoginLost)
                    {
                        a.LoginLost = true;
                        if (Store.Settings.WebhookLocked)
                            Webhook.Send("Cookie wygaslo", AppState.Name(a) + ": wymagane ponowne logowanie.", Webhook.Red);
                    }
                }
                else
                {
                    a.LoginLost = false;
                    a.Username = u.Name;
                    a.DisplayName = u.Display;
                    a.UserId = u.Id;
                }
            }
            Store.SaveAccounts();
            AddLog("Sprawdzono cookies (" + copy.Count + "): OK=" + (copy.Count - bad - locked) + ", wygasle=" + bad + ", 403=" + locked);
        }

        /// <summary>Queries Roblox Presence API in batches to update live InGame / Website / Offline badges.</summary>
        public static async Task<int> CheckPresenceAsync()
        {
            List<Account> withIds = Store.Accounts.Where(a => a.UserId > 0).ToList();
            if (withIds.Count == 0) return 0;
            Account authAcc = Store.Accounts.FirstOrDefault(a => !string.IsNullOrEmpty(a.Cookie));
            string cookie = authAcc != null ? authAcc.Cookie : "";
            Dictionary<long, PresenceInfo> map = await Api.Presence(cookie, withIds.Select(a => a.UserId).ToList());
            int inGame = 0;
            foreach (Account a in withIds)
            {
                PresenceInfo pi;
                if (map.TryGetValue(a.UserId, out pi))
                {
                    LiveInfo li = AppState.Live(a);
                    li.PresenceType = pi.Type;
                    li.Where = !string.IsNullOrEmpty(pi.Location)
                        ? pi.Location
                        : (pi.PlaceId > 0 ? "Place " + pi.PlaceId : "");
                    if (pi.Type == 2) inGame++;
                }
            }
            AddLog("Presence: sprawdzono " + withIds.Count + " kont (w grze: " + inGame + ").");
            return inGame;
        }

        static async Task RunSchedulerTickAsync()
        {
            AppSettings s = Store.Settings;
            if (!s.ScheduleEnabled) return;

            string today = DateTime.Now.ToString("yyyy-MM-dd");
            string hm = DateTime.Now.ToString("HH:mm");
            List<Account> targets = string.IsNullOrEmpty(s.ScheduleGroup)
                ? Store.Accounts.ToList()
                : Store.Accounts.Where(x => (x.Group ?? "").Equals(s.ScheduleGroup, StringComparison.OrdinalIgnoreCase)).ToList();

            if (!string.IsNullOrWhiteSpace(s.ScheduleStartTime) && lastSchedStartDate != today && hm == s.ScheduleStartTime.Trim())
            {
                lastSchedStartDate = today;
                if (targets.Count > 0 && !LaunchBusy)
                {
                    AddLog("Harmonogram: automatyczny start kont (" + targets.Count + ").");
                    Forget(Task.Run(async delegate { await LaunchAccountsAsync(targets, null); }));
                }
            }

            if (!string.IsNullOrWhiteSpace(s.ScheduleStopTime) && lastSchedStopDate != today && hm == s.ScheduleStopTime.Trim())
            {
                lastSchedStopDate = today;
                AddLog("Harmonogram: zatrzymanie wszystkich sesji o " + hm + ".");
                StopAll();
            }

            if (s.ScheduleRestartHours > 0 && Launcher.RobloxPids().Count > 0)
            {
                if ((DateTime.UtcNow - lastScheduledRestart).TotalHours >= s.ScheduleRestartHours && !LaunchBusy)
                {
                    lastScheduledRestart = DateTime.UtcNow;
                    AddLog("Harmonogram: cykliczny restart co " + s.ScheduleRestartHours + "h.");
                    StopAll();
                    await Task.Delay(3000);
                    if (targets.Count > 0)
                        Forget(Task.Run(async delegate { await LaunchAccountsAsync(targets, null); }));
                }
            }
        }

        public static LaunchSpec BuildCurrentSpec()
        {
            AppSettings s = Store.Settings;
            LaunchSpec spec = new LaunchSpec();
            spec.Kind = string.IsNullOrEmpty(s.LastMode) ? "Game" : s.LastMode;
            if (spec.Kind == "Game" || spec.Kind == "Job")
            {
                spec.PlaceId = Util.ParsePlaceId(s.LastPlaceId);
                spec.JobId = (s.LastJobId ?? "").Trim();
            }
            else if (spec.Kind == "Private")
            {
                spec.LinkCode = (s.LastPrivate ?? "").Trim();
                spec.PlaceId = Util.ParsePlaceId(spec.LinkCode);
            }
            else if (spec.Kind == "Follow")
            {
                spec.FollowName = (s.LastFollow ?? "").Trim();
            }
            return spec;
        }

        /// <summary>Sequentially launches the requested accounts using per-account overrides or the active global target.</summary>
        public static async Task LaunchAccountsAsync(List<Account> accounts, LaunchSpec explicitSpec)
        {
            if (LaunchBusy || accounts == null || accounts.Count == 0) return;
            LaunchBusy = true;
            QueueTotal = accounts.Count;
            QueueDone = 0;
            QueueCurrent = "";
            launchCts = new CancellationTokenSource();
            CancellationToken ct = launchCts.Token;

            try
            {
                if (!Store.Settings.MultiInstance && accounts.Count > 1)
                {
                    MultiInstance.Start();
                    Store.Settings.MultiInstance = true;
                    Store.SaveSettings();
                }

                LaunchSpec fallback = explicitSpec ?? BuildCurrentSpec();
                for (int i = 0; i < accounts.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    Account a = accounts[i];
                    QueueCurrent = AppState.Name(a);
                    LaunchSpec spec = ResolveSpecForAccount(a, fallback);
                    if (spec == null || (spec.Kind != "Follow" && spec.Kind != "Private" && spec.PlaceId <= 0))
                    {
                        AddLog("[" + AppState.Name(a) + "] pominieto: brak poprawnego Place ID / gry.");
                        QueueDone++;
                        continue;
                    }

                    bool ok = await Launcher.LaunchAsync(a, spec, AddLog);
                    if (ok)
                    {
                        TotalLaunches++;
                        a.LastUsedTicks = DateTime.UtcNow.Ticks;
                        int pid = Launcher.LatestPid(a);
                        if (pid > 0 && (a.SeqMinimize || a.SeqTrim))
                        {
                            Forget(Task.Run(async delegate { await Launcher.RunSequence(a, pid, AddLog); }));
                        }
                    }
                    QueueDone++;

                    if (i + 1 < accounts.Count && !ct.IsCancellationRequested)
                    {
                        int delaySec = a.LaunchDelaySec > 0 ? a.LaunchDelaySec : Math.Max(0, Store.Settings.LaunchDelaySec);
                        if (delaySec > 0) await Task.Delay(delaySec * 1000, ct);
                    }
                }

                Store.SaveAccounts();
                if (Store.Settings.AutoTileOnBatch && !ct.IsCancellationRequested)
                {
                    await Task.Delay(2500);
                    ApplyLayout();
                }
                if (Store.Settings.WebhookBatch)
                    Webhook.SendStatus("Batch uruchomiony", "Uruchomiono " + QueueDone + " z " + QueueTotal + " kont.", Webhook.Green);
            }
            catch (TaskCanceledException) { AddLog("Kolejka uruchamiania zatrzymana."); }
            catch (Exception ex) { Log.Error("LaunchAccountsAsync", ex); AddLog("Blad kolejki: " + ex.Message); }
            finally
            {
                LaunchBusy = false;
                QueueCurrent = "";
            }
        }

        static LaunchSpec ResolveSpecForAccount(Account a, LaunchSpec fallback)
        {
            if (Store.Settings.UseSavedPerAccount && (!string.IsNullOrEmpty(a.SavedPlaceId) || !string.IsNullOrEmpty(a.GameLink)))
            {
                string raw = !string.IsNullOrEmpty(a.GameLink) ? a.GameLink : a.SavedPlaceId;
                long pid = Util.ParsePlaceId(raw);
                if (pid > 0)
                {
                    LaunchSpec s = new LaunchSpec();
                    s.PlaceId = pid;
                    if (!string.IsNullOrEmpty(a.SavedJobId))
                    {
                        s.Kind = "Job";
                        s.JobId = a.SavedJobId;
                    }
                    else s.Kind = "Game";
                    return s;
                }
            }
            return fallback != null ? fallback.Copy() : null;
        }

        public static void StopLaunchQueue()
        {
            try { if (launchCts != null) launchCts.Cancel(); } catch (Exception) { }
        }

        public static void StopAll()
        {
            StopLaunchQueue();
            int n = Launcher.KillAllRoblox();
            AddLog("Zatrzymano wszystkie procesy Roblox (" + n + ").");
        }

        public static void StopAccount(string accountIdOrUser)
        {
            Account a = Store.Accounts.FirstOrDefault(x => x.Id == accountIdOrUser || x.Username.Equals(accountIdOrUser, StringComparison.OrdinalIgnoreCase));
            if (a != null)
            {
                Launcher.CloseInstancesOf(a, AddLog);
                Launcher.TurboAfterClose();
            }
        }

        /// <summary>Computes and applies the configured window tiling layout across all active Roblox windows.</summary>
        public static int ApplyLayout()
        {
            int moved = 0;
            try
            {
                using (new DpiScope())
                {
                    List<WinInfo> wins = WindowLayout.FindRobloxWindows().Where(w => w.HasWindow).ToList();
                    if (wins.Count == 0) return 0;
                    List<MonitorDesc> mons = WindowLayout.GetMonitors();
                    MonitorDesc mon = WindowLayout.PickMonitor(mons, Store.Settings.Layout.Monitor);
                    Rectangle area = Store.Settings.Layout.UseWorkArea ? mon.Work : mon.Bounds;
                    bool overlap;
                    List<Rectangle> rects = WindowLayout.ComputeRects(
                        wins.Count,
                        area,
                        Store.Settings.Layout,
                        Store.Settings.Layout.MinW > 0 ? Store.Settings.Layout.MinW : 200,
                        Store.Settings.Layout.MinH > 0 ? Store.Settings.Layout.MinH : 150,
                        out overlap);
                    for (int i = 0; i < wins.Count && i < rects.Count; i++)
                    {
                        WindowLayout.Place(wins[i], rects[i], Store.Settings.Layout, overlap);
                        if (Store.Settings.Layout.LabelTitles && !string.IsNullOrEmpty(wins[i].Account))
                            WindowLayout.SetTitle(wins[i].Hwnd, "Roblox - " + wins[i].Account);
                        moved++;
                    }
                }
                AddLog("Ulozono okna Roblox (" + moved + ").");
            }
            catch (Exception ex) { Log.Error("ApplyLayout", ex); }
            return moved;
        }

        public static bool StartMemHelper()
        {
            if (MemClean.HelperAlive()) return true;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, "--memhelper " + Process.GetCurrentProcess().Id + " --datadir \"" + Store.Dir + "\"");
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.ErrorDialog = false;
                if (!MemClean.IsElevated()) psi.Verb = "runas";
                Process.Start(psi);
                return true;
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode != 1223) Log.Error("StartMemHelper", ex);
                return false;
            }
            catch (Exception ex) { Log.Error("StartMemHelper", ex); return false; }
        }

        public static async Task<string> CleanMemoryAsync()
        {
            if (memBusy) return "Czyszczenie RAM juz trwa.";
            memBusy = true;
            try
            {
                if (MemClean.HelperAlive())
                {
                    File.WriteAllText(MemClean.RequestPath, "clean");
                    for (int i = 0; i < 40; i++)
                    {
                        MemStatus st = MemClean.ReadStatus();
                        if (st != null && st.LastTicks != memSeen) break;
                        await Task.Delay(250);
                    }
                    CheckMemResult();
                    return "Wyczyszczono RAM przez usluge pomocnicza.";
                }
                else
                {
                    long freed = MemClean.TrimProcesses(Launcher.RobloxPids());
                    DailyCleans++;
                    string msg = "Przycieto pamiec procesow Roblox: zwolniono " + Util.FormatBytes(freed);
                    AddLog(msg);
                    return msg;
                }
            }
            catch (Exception ex) { return "Blad RAM: " + ex.Message; }
            finally { memBusy = false; }
        }

        static void CheckMemResult()
        {
            MemStatus st = MemClean.ReadStatus();
            if (st == null || st.LastTicks == memSeen) return;
            memSeen = st.LastTicks;
            DailyCleans++;
            AddLog("RAM wyczyszczony (" + st.Reason + "): zwolniono " + Util.FormatBytes(st.LastFreed) + ", uzycie " + st.LoadBefore + "% -> " + st.LoadAfter + "%.");
        }

        static void Forget(Task t)
        {
            if (t == null) return;
            t.ContinueWith(delegate(Task x)
            {
                if (x.Exception != null) Log.Error("AppEngine.Bg", x.Exception.GetBaseException());
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
