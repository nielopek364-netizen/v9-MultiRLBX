using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MultiRLBX
{
    /// <summary>
    /// Holds Roblox's named synchronization objects (<c>ROBLOX_singletonMutex</c> and
    /// <c>ROBLOX_singletonEvent</c>) externally so multiple Roblox clients can coexist cleanly
    /// without injecting code or modifying client memory.
    /// </summary>
    public static class MultiInstance
    {
        static Thread thread;
        static volatile bool stop;
        static volatile string state = "Off";

        public static string State { get { return state; } }
        public static bool Active { get { return state == "Active"; } }
        public static bool Enabled { get { return thread != null; } }

        /// <summary>Starts the background holder thread before launching multiple Roblox clients.</summary>
        public static void Start()
        {
            if (thread != null) return;
            stop = false;
            state = "Waiting";
            thread = new Thread(Run);
            thread.IsBackground = true;
            thread.Name = "MultiInstanceHolder";
            thread.Start();
        }

        public static void Stop()
        {
            if (thread == null) return;
            stop = true;
            try { thread.Join(2000); } catch (Exception) { }
            thread = null;
            state = "Off";
        }

        static void Run()
        {
            Mutex mutex = null;
            EventWaitHandle ev = null;
            bool owned = false;
            try
            {
                bool created;
                ev = new EventWaitHandle(false, EventResetMode.ManualReset, "ROBLOX_singletonEvent", out created);
                mutex = new Mutex(false, "ROBLOX_singletonMutex");
                while (!stop)
                {
                    bool got = false;
                    try { got = mutex.WaitOne(400); }
                    catch (AbandonedMutexException) { got = true; }
                    if (got)
                    {
                        owned = true;
                        state = "Active";
                        while (!stop) Thread.Sleep(250);
                    }
                    else
                    {
                        state = "Waiting";
                    }
                }
            }
            catch (Exception ex)
            {
                state = "Error: " + ex.Message;
                Log.Error("MultiInstance", ex);
            }
            finally
            {
                try { if (owned && mutex != null) mutex.ReleaseMutex(); } catch (Exception) { }
                try { if (mutex != null) mutex.Close(); } catch (Exception) { }
                try { if (ev != null) ev.Close(); } catch (Exception) { }
                if (!state.StartsWith("Error")) state = "Off";
            }
        }
    }

    /// <summary>
    /// Manages Roblox process discovery, ticket-authenticated launching, <c>ClientAppSettings.json</c>
    /// Fast Flag deployment, FPS cap guarding, crash-loop breaker tracking, and Watchdog recovery.
    /// </summary>
    public static class Launcher
    {
        public static readonly object InstancesLock = new object();
        public static readonly List<Instance> Instances = new List<Instance>();
        static readonly Random Rng = new Random();
        const string ProcName = "RobloxPlayerBeta";

        /// <summary>Returns a thread-safe shallow copy of currently tracked client <see cref="Instance"/> records.</summary>
        public static List<Instance> SnapshotInstances()
        {
            lock (InstancesLock) { return Instances.ToList(); }
        }

        public static bool IsAccountRunning(Account a)
        {
            if (a == null) return false;
            lock (InstancesLock)
            {
                for (int i = 0; i < Instances.Count; i++)
                {
                    Instance inst = Instances[i];
                    if (inst.Account == a && IsAlive(inst.Pid)) return true;
                }
            }
            return false;
        }

        // ---------- crash breaker ----------
        // Counts how often the watcher has relaunched an account inside a short window. Once it reaches the
        // limit the account is left alone for a cooldown, so a client that keeps crashing never loops forever.
        class BreakerState { public int Count; public DateTime WindowStart; public bool GaveUp; public DateTime GaveUpAt; }
        static readonly Dictionary<string, BreakerState> breaker = new Dictionary<string, BreakerState>();

        static BreakerState Breaker(Account a)
        {
            BreakerState s;
            if (!breaker.TryGetValue(a.Id, out s)) { s = new BreakerState(); s.WindowStart = DateTime.UtcNow; breaker[a.Id] = s; }
            return s;
        }

        // May the watcher relaunch this account right now? (false = the breaker has given up for a while)
        public static bool BreakerAllows(Account a)
        {
            if (!Store.Settings.CrashBreaker) return true;
            BreakerState s = Breaker(a);
            if (s.GaveUp)
            {
                if ((DateTime.UtcNow - s.GaveUpAt).TotalMinutes >= Math.Max(1, Store.Settings.CrashBreakerCooldownMin))
                {
                    s.GaveUp = false; s.Count = 0; s.WindowStart = DateTime.UtcNow;   // cooldown over: try again
                    return true;
                }
                return false;
            }
            return true;
        }

        // Record one watcher relaunch; returns true if that tipped the account over the limit (just gave up).
        public static bool BreakerRecord(Account a)
        {
            if (!Store.Settings.CrashBreaker) return false;
            BreakerState s = Breaker(a);
            int windowMin = Math.Max(1, Store.Settings.CrashBreakerWindowMin);
            if ((DateTime.UtcNow - s.WindowStart).TotalMinutes > windowMin) { s.WindowStart = DateTime.UtcNow; s.Count = 0; }
            s.Count++;
            if (s.Count >= Math.Max(1, Store.Settings.CrashBreakerMax)) { s.GaveUp = true; s.GaveUpAt = DateTime.UtcNow; return true; }
            return false;
        }

        // A deliberate (user) launch clears the breaker for that account.
        public static void BreakerClear(Account a)
        {
            breaker.Remove(a.Id);
        }

        // ---------- process helpers ----------
        public static List<int> RobloxPids()
        {
            List<int> pids = new List<int>();
            foreach (Process p in Process.GetProcessesByName(ProcName))
            {
                pids.Add(p.Id);
                p.Dispose();
            }
            return pids;
        }

        // Uses the process snapshot only (no process handle), so it also works for protected Roblox processes.
        public static bool IsAlive(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    return p.ProcessName.Equals(ProcName, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception) { return false; }
        }

        public static void KillPid(int pid)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    if (p.ProcessName.Equals(ProcName, StringComparison.OrdinalIgnoreCase)) p.Kill();
                }
            }
            catch (ArgumentException) { }
            catch (Exception ex) { Log.Write("Could not close Roblox process " + pid + ": " + ex.Message); }
        }

        // Closes every Roblox client and the Roblox bootstrapper (used before turning multi-instance on).
        public static int KillAllRoblox()
        {
            int n = 0;
            foreach (string name in new string[] { ProcName, "RobloxPlayerLauncher", "RobloxCrashHandler" })
            {
                foreach (Process p in Process.GetProcessesByName(name))
                {
                    try { p.Kill(); n++; } catch (Exception) { }
                    p.Dispose();
                }
            }
            lock (InstancesLock) { Instances.Clear(); }
            return n;
        }

        public static void CloseInstancesOf(Account a, Action<string> log)
        {
            List<Instance> mine;
            lock (InstancesLock) { mine = Instances.Where(i => i.Account == a).ToList(); }
            foreach (Instance i in mine)
            {
                i.UserClosed = true;
                if (IsAlive(i.Pid))
                {
                    KillPid(i.Pid);
                    if (log != null) log("[" + AppState.Name(a) + "] closed previous Roblox window (PID " + i.Pid + ")");
                }
                lock (InstancesLock) { Instances.Remove(i); }
            }
        }

        public static Instance FindInstance(int pid)
        {
            lock (InstancesLock) { return Instances.FirstOrDefault(i => i.Pid == pid); }
        }

        // Finds the Roblox client that carries this account's stable browser-tracker id on its command line.
        // This identifies the right process even when Roblox reused a background process (no new PID appears).
        // Returns 0 if command lines can't be read (e.g. the process is protected) - the caller then falls back.
        public static int FindByTracker(long tracker)
        {
            if (tracker <= 0) return 0;
            string needle = tracker.ToString();
            try
            {
                using (System.Management.ManagementObjectSearcher searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'RobloxPlayerBeta.exe'"))
                using (System.Management.ManagementObjectCollection results = searcher.Get())
                {
                    foreach (System.Management.ManagementObject mo in results)
                    {
                        try
                        {
                            object cl = mo["CommandLine"];
                            if (cl == null) continue;
                            if (cl.ToString().IndexOf(needle, StringComparison.Ordinal) >= 0)
                            {
                                object pidObj = mo["ProcessId"];
                                if (pidObj != null) return Convert.ToInt32(pidObj);
                            }
                        }
                        finally { mo.Dispose(); }
                    }
                }
            }
            catch (Exception) { }
            return 0;
        }

        // True if WMI lets us read any RobloxPlayerBeta command line at all (used for diagnostics).
        public static bool CanReadCommandLines()
        {
            try
            {
                using (System.Management.ManagementObjectSearcher s = new System.Management.ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE Name = 'RobloxPlayerBeta.exe'"))
                using (System.Management.ManagementObjectCollection r = s.Get())
                    foreach (System.Management.ManagementObject mo in r)
                    {
                        try { if (mo["CommandLine"] != null) return true; } finally { mo.Dispose(); }
                    }
            }
            catch (Exception) { }
            return false;
        }

        // ---------- locating Roblox ----------
        static string ExtractExe(string command)
        {
            if (string.IsNullOrEmpty(command)) return null;
            command = command.Trim();
            if (command.StartsWith("\""))
            {
                int end = command.IndexOf('"', 1);
                if (end > 1) return command.Substring(1, end - 1);
                return null;
            }
            int sp = command.IndexOf(' ');
            return sp > 0 ? command.Substring(0, sp) : command;
        }

        public static string FindRobloxExe()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\roblox-player\shell\open\command"))
                {
                    if (k != null)
                    {
                        string handler = ExtractExe(k.GetValue("") as string);
                        if (handler != null)
                        {
                            string dir = Path.GetDirectoryName(handler);
                            if (dir != null)
                            {
                                string beta = Path.Combine(dir, ProcName + ".exe");
                                if (File.Exists(beta)) return beta;
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions");
                if (Directory.Exists(root))
                {
                    string best = null;
                    DateTime bestTime = DateTime.MinValue;
                    foreach (string d in Directory.GetDirectories(root))
                    {
                        string exe = Path.Combine(d, ProcName + ".exe");
                        if (!File.Exists(exe)) continue;
                        DateTime t = File.GetLastWriteTime(exe);
                        if (t > bestTime) { bestTime = t; best = exe; }
                    }
                    if (best != null) return best;
                }
            }
            catch (Exception) { }
            return null;
        }

        // True if the roblox-player URL handler points at the old bootstrapper, which closes other clients.
        public static bool UsesLegacyBootstrapper()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\roblox-player\shell\open\command"))
                {
                    if (k == null) return false;
                    string v = (k.GetValue("") as string) ?? "";
                    return v.IndexOf("RobloxPlayerLauncher", StringComparison.OrdinalIgnoreCase) >= 0
                        || v.IndexOf("RobloxPlayerInstaller", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception) { return false; }
        }

        // ---------- client settings (graphics flags) ----------
        // Writes the allowlisted flags chosen on the Performance page into Roblox's ClientAppSettings.json.
        // Called right before every launch, so the same settings apply to every account until changed.
        public static void WriteClientSettings(string exe)
        {
            try
            {
                List<string> ignored;
                Dictionary<string, object> flags = Gfx.Final(out ignored);
                string dir = Path.Combine(Path.GetDirectoryName(exe), "ClientSettings");
                string file = Path.Combine(dir, "ClientAppSettings.json");
                if (flags.Count == 0)
                {
                    // user wants Roblox defaults: only remove a file that this app wrote earlier
                    if (Store.Settings.WroteClientFile)
                    {
                        if (File.Exists(file)) File.Delete(file);
                        Store.Settings.WroteClientFile = false;
                        Store.SaveSettings();
                    }
                    return;
                }
                Directory.CreateDirectory(dir);
                File.WriteAllText(file, Json.To(flags));
                if (!Store.Settings.WroteClientFile)
                {
                    Store.Settings.WroteClientFile = true;
                    Store.SaveSettings();
                }
            }
            catch (Exception ex) { Log.Write("Could not write ClientAppSettings.json: " + ex.Message); }
        }

        public static string RemoveClientSettings()
        {
            string exe = FindRobloxExe();
            if (exe == null) return "Roblox was not found.";
            try
            {
                string f = Path.Combine(Path.GetDirectoryName(exe), "ClientSettings", "ClientAppSettings.json");
                Store.Settings.WroteClientFile = false;
                Store.SaveSettings();
                if (File.Exists(f)) { File.Delete(f); return "Roblox default settings restored (deleted " + f + ")."; }
                return "Roblox default settings restored (there was no settings file).";
            }
            catch (Exception ex) { return "Failed: " + ex.Message; }
        }

        // ---------- FPS cap (Roblox's own settings file, NOT a Fast Flag) ----------
        // Roblox keeps its "Max frame rate" in %LOCALAPPDATA%\Roblox\GlobalBasicSettings_13.xml as
        // <int name="FramerateCap">-1</int>. Changing it before a client starts is what the manual
        // "edit the XML in Notepad" method does. Roblox reads it at startup.
        internal static string FindBasicSettings()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox");
                if (!Directory.Exists(dir)) return null;
                string prefix = "GlobalBasicSettings_";
                string best = null;
                int bestNum = -1;
                foreach (string f in Directory.GetFiles(dir, prefix + "*.xml"))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    int n;
                    if (!int.TryParse(name.Substring(prefix.Length), out n)) continue;   // skips the Studio file
                    if (n > bestNum) { bestNum = n; best = f; }
                }
                return best;
            }
            catch (Exception) { return null; }
        }

        public static void ApplyFpsCap(Action<string> log)
        {
            SetFpsCap(Store.Settings.FpsCap, log);
        }

        const string FpsPattern = "(<int name=\"FramerateCap\">)(-?\\d+)(</int>)";

        // Puts "want" into Roblox's settings file (one attempt) and reads it back.
        // Returns "" when the file now holds that value, otherwise the reason it does not.
        internal static string TryWriteCap(string file, int want, out bool changed)
        {
            changed = false;
            try
            {
                string xml = File.ReadAllText(file, Encoding.UTF8);
                Match m = Regex.Match(xml, FpsPattern);
                if (!m.Success) return "the settings file has no FramerateCap entry yet - start Roblox once normally, close it, then try again";
                int have;
                int.TryParse(m.Groups[2].Value, out have);
                if (have != want)
                {
                    string backup = file + ".multihub-backup";
                    if (!File.Exists(backup)) File.Copy(file, backup);
                    xml = xml.Substring(0, m.Groups[2].Index) + want + xml.Substring(m.Groups[2].Index + m.Groups[2].Length);
                    File.WriteAllText(file, xml, new UTF8Encoding(false));
                    changed = true;
                }
                Match check = Regex.Match(File.ReadAllText(file, Encoding.UTF8), FpsPattern);
                return (check.Success && check.Groups[2].Value == want.ToString()) ? "" : "Roblox changed the file again right away";
            }
            catch (IOException ex) { return ex.Message; }
            catch (UnauthorizedAccessException ex) { return ex.Message; }
        }

        // Writes one specific cap (0 = give control back to Roblox). Used for the global cap and for per-account caps.
        // Retries when Roblox still has the file open and checks that the value really is in the file afterwards.
        public static void SetFpsCap(int cap, Action<string> log)
        {
            bool restore = cap <= 0;
            if (restore && !Store.Settings.WroteFpsCap) return;       // never asked for a cap: leave Roblox alone
            string file = FindBasicSettings();
            if (file == null)
            {
                if (!restore && log != null) log("FPS cap: Roblox's settings file was not found. Start Roblox once normally, close it, then try again.");
                return;
            }
            int want = restore ? -1 : cap;
            string problem = "";
            bool changed = false;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                bool c;
                problem = TryWriteCap(file, want, out c);
                if (c) changed = true;
                if (problem.Length == 0) break;
                Thread.Sleep(150);
            }
            if (problem.Length > 0)
            {
                if (log != null) log("Could not set the FPS cap in Roblox's settings file: " + problem);
                return;
            }
            if (restore)
            {
                Store.Settings.WroteFpsCap = false;
                Store.SaveSettings();
                if (changed && log != null) log("FPS cap removed: Roblox uses its own frame rate setting again.");
            }
            else
            {
                if (!Store.Settings.WroteFpsCap) { Store.Settings.WroteFpsCap = true; Store.SaveSettings(); }
                if (changed && log != null) log("FPS cap " + cap + " written to Roblox's settings file.");
            }
        }

        // Waits until the Roblox window of that process exists (the client has read its settings by then).
        static async Task WaitForWindow(int pid, int timeoutMs)
        {
            DateTime end = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < end)
            {
                foreach (WinInfo w in WindowLayout.FindRobloxWindows())
                    if (w.Pid == pid && w.HasWindow) return;
                await Task.Delay(700);
            }
        }

        // ---------- CPU priority ----------
        public static bool ApplyPriority(int pid, bool includeNormal)
        {
            ProcessPriorityClass pc;
            string s = Store.Settings.Priority;
            if (s == "BelowNormal") pc = ProcessPriorityClass.BelowNormal;
            else if (s == "Idle") pc = ProcessPriorityClass.Idle;
            else if (includeNormal) pc = ProcessPriorityClass.Normal;
            else return true;
            try
            {
                using (Process p = Process.GetProcessById(pid)) { p.PriorityClass = pc; }
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Could not change the CPU priority of process " + pid + " (Roblox may protect it): " + ex.Message);
                return false;
            }
        }

        public static int ApplyPriorityAll()
        {
            int ok = 0;
            foreach (int pid in RobloxPids())
                if (ApplyPriority(pid, true)) ok++;
            return ok;
        }

        // ---------- launching ----------
        // Same shape as Roblox Account Manager: 6 digits (100000-174999) followed by 6 digits (100000-899999).
        static long RandomTracker()
        {
            return long.Parse(Rng.Next(100000, 175000).ToString() + Rng.Next(100000, 900000).ToString());
        }

        // The join request the Roblox client sends to Roblox, built exactly like Roblox Account Manager does
        // (same parameters in the same order). Accounts launched like that are not asked for human verification.
        public static string PlaceLauncherUrl(LaunchSpec s, long tracker)
        {
            string b = Store.Settings.PlaceLauncherUrl;
            if (string.IsNullOrWhiteSpace(b)) b = "https://assetgame.roblox.com/game/PlaceLauncher.ashx";
            if (s.Kind == "Job")
                return b + "?request=RequestGameJob&browserTrackerId=" + tracker + "&placeId=" + s.PlaceId + "&gameId=" + s.JobId + "&isPlayTogetherGame=false";
            if (s.Kind == "Private")
                return b + "?request=RequestPrivateGame&placeId=" + s.PlaceId + "&linkCode=" + s.LinkCode;
            if (s.Kind == "Follow")
                return b + "?request=RequestFollowUser&userId=" + s.FollowUserId;
            return b + "?request=RequestGame&browserTrackerId=" + tracker + "&placeId=" + s.PlaceId + "&isPlayTogetherGame=false";
        }

        // Same encoding as .NET's HttpUtility.UrlEncode (lower-case hex, space = '+'), which Roblox Account Manager uses.
        static string UrlEncode(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (byte b in Encoding.UTF8.GetBytes(s))
            {
                char c = (char)b;
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || "-_.!*()".IndexOf(c) >= 0) sb.Append(c);
                else if (c == ' ') sb.Append('+');
                else sb.Append('%').Append(((int)b).ToString("x2"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Authenticates the account cookie, applies graphics/FPS guards, launches Roblox via
        /// the <c>roblox-player:</c> Protocol URI, and polls for the resulting PID using WMI tracker matching.
        /// </summary>
        public static async Task<bool> LaunchAsync(Account a, LaunchSpec spec, Action<string> log)
        {
            string who = "[" + AppState.Name(a) + "] ";
            FpsGuard guard = null;
            try
            {
                if (string.IsNullOrEmpty(a.Cookie)) { log(who + "has no cookie saved."); return false; }

                int max = Store.Settings.MaxInstances;
                if (max > 0 && RobloxPids().Count >= max)
                {
                    log(who + "not launched: Max instances (" + max + ") reached. Close one or raise the limit in Settings.");
                    return false;
                }

                int minFree = Store.Settings.Mem.MinFreeRamMb;
                if (minFree > 0)
                {
                    int mload; long mtotal, mavail;
                    if (MemClean.Status(out mload, out mtotal, out mavail) && mavail < (long)minFree * 1048576)
                    {
                        log(who + "not launched: only " + Util.FormatBytes(mavail) + " of RAM is free (limit " + minFree + " MB - Performance page).");
                        GuardAlert(mavail, minFree);
                        return false;
                    }
                }

                if (Store.Settings.RefreshCookieOnLaunch)
                {
                    string nc = await Api.RotateCookie(a.Cookie, log);
                    if (nc != null) { a.Cookie = nc; Store.SaveAccounts(); log(who + "cookie refreshed."); }
                }

                log(who + "requesting login ticket...");
                TicketResult t = await Api.GetTicket(a.Cookie);
                if (t.Ticket == null)
                {
                    log(who + "failed: " + t.Error);
                    if (t.Unauthorized) AppState.Live(a).CookieBad = true;
                    if (t.Locked)
                    {
                        LiveInfo li = AppState.Live(a);
                        if (!li.LockedAlerted && Store.Settings.WebhookLocked)
                        {
                            Webhook.Send("Account locked", AppState.Name(a) + ": Roblox returned HTTP 403 (locked / blocked). Unlock it on roblox.com.", Webhook.Red);
                            li.LockedAlerted = true;
                        }
                        li.Locked = true;
                        log(who + "this account looks LOCKED / blocked by Roblox (HTTP 403). Unlock it on roblox.com.");
                    }
                    return false;
                }
                AppState.Live(a).CookieBad = false;
                AppState.Live(a).Locked = false;
                AppState.Live(a).LockedAlerted = false;

                if (Store.Settings.AutoCloseLast) CloseInstancesOf(a, log);
                if (a.TrackerId == 0) a.TrackerId = RandomTracker();

                if (!MultiInstance.Active && RobloxPids().Count > 0)
                    log(who + "WARNING: multi-instance is not active, so starting this will likely close your other Roblox windows.");

                string exe = FindRobloxExe();
                if (exe != null) WriteClientSettings(exe);
                if (a.FpsCap > 0)
                {
                    SetFpsCap(a.FpsCap, log);                      // this account has its own cap for this launch ...
                    guard = new FpsGuard(a.FpsCap, log);           // ... and nothing may overwrite it while the client starts
                }
                else if (Store.Settings.MaxAlts && Store.Settings.MaxAltsFps > 0)
                {
                    // Max alts is on: new windows start cheap (low frame rate) from the first second.
                    // The cap stays for that window until it is launched again with the switch off.
                    SetFpsCap(Store.Settings.MaxAltsFps, log);
                    guard = new FpsGuard(Store.Settings.MaxAltsFps, log);
                }
                else ApplyFpsCap(log);

                List<int> before = RobloxPids();
                long launchTime = Util.NowMs();
                string url = PlaceLauncherUrl(spec, a.TrackerId);

                try
                {
                    string proto = "roblox-player:1+launchmode:play+gameinfo:" + t.Ticket + "+launchtime:" + launchTime
                        + "+placelauncherurl:" + UrlEncode(url) + "+browsertrackerid:" + a.TrackerId
                        + "+robloxLocale:en_us+gameLocale:en_us+channel:+LaunchExp:InApp";
                    ProcessStartInfo psi = new ProcessStartInfo(proto);
                    psi.UseShellExecute = true;
                    await Task.Run(delegate { using (Process pr = Process.Start(psi)) { } });
                }
                catch (Exception ex)
                {
                    log(who + "could not start Roblox: " + ex.Message);
                    if (guard != null) { guard.Dispose(); ApplyFpsCap(log); }
                    return false;
                }

                log(who + "Roblox is starting (" + spec.Describe() + ")...");
                // Which Roblox processes already had a visible window before we launched.
                HashSet<int> windowedBefore = new HashSet<int>();
                foreach (WinInfo w0 in WindowLayout.FindRobloxWindows())
                    if (w0.HasWindow) windowedBefore.Add(w0.Pid);

                // Poll for the client (UI thread - Roblox is STARTED on a background thread above, so the pump
                // isn't blocked; and the shared Instances list is only touched on the UI thread).
                int pid = 0;
                bool already = false;
                for (int i = 0; i < 120 && pid == 0; i++)
                {
                    await Task.Delay(500);

                    // Best signal: the process whose command line carries this account's tracker id. This finds
                    // the right client whether Roblox spawned a new process or REUSED a background one.
                    int tp = FindByTracker(a.TrackerId);
                    if (tp != 0)
                    {
                        Instance ex = FindInstance(tp);
                        if (ex == null) { pid = tp; break; }
                        if (ex.Account == a) { pid = tp; already = true; break; }
                    }
                    // Fallback 1: a brand-new process.
                    foreach (int p in RobloxPids())
                        if (!before.Contains(p) && FindInstance(p) == null) { pid = p; break; }
                    // Fallback 2: an untracked process that just gained a window (reuse, when the tracker can't be read).
                    if (pid == 0)
                        foreach (WinInfo w in WindowLayout.FindRobloxWindows())
                            if (w.HasWindow && !windowedBefore.Contains(w.Pid) && FindInstance(w.Pid) == null) { pid = w.Pid; break; }
                }

                a.LastUsedTicks = DateTime.UtcNow.Ticks;
                Store.SaveAccounts();

                if (pid == 0)
                {
                    // Diagnostic dump so the real cause is visible in the log instead of guessed at.
                    System.Text.StringBuilder diag = new System.Text.StringBuilder();
                    foreach (int p in RobloxPids()) diag.Append(p).Append(FindInstance(p) != null ? "(tracked) " : " ");
                    int win = 0; foreach (WinInfo w in WindowLayout.FindRobloxWindows()) if (w.HasWindow) win++;
                    log(who + "NOT detected. Roblox procs: [" + diag.ToString().Trim() + "]  windows:" + win
                        + "  trackerId:" + a.TrackerId + "  cmdline-readable:" + Launcher.CanReadCommandLines()
                        + ". If a window is actually open, press 'Stop all' or close it in Task Manager, then relaunch.");
                    if (guard != null) { guard.Dispose(); ApplyFpsCap(log); }
                    return false;
                }
                if (already)
                {
                    Instance exi = FindInstance(pid);
                    if (exi != null) { exi.UserClosed = false; exi.Spec = spec.Copy(); exi.Account = a; }
                    ApplyPriority(pid, false);
                    log(who + "re-used its running window (PID " + pid + ").");
                    if (guard != null) { guard.Dispose(); ApplyFpsCap(log); }
                    return true;
                }
                Instance inst = new Instance();
                inst.Account = a;
                inst.Pid = pid;
                inst.Started = DateTime.Now;
                inst.Spec = spec.Copy();
                lock (InstancesLock) { Instances.Add(inst); }
                ApplyPriority(pid, false);
                Audit.Write("LAUNCH " + AppState.Name(a) + " (PID " + pid + ")");
                log(who + "started (PID " + pid + ").");
                if (guard != null)
                {
                    // Wait until the Roblox window is really up (the client has read its settings by then), then put the
                    // global cap back so the next account gets the global one again.
                    await WaitForWindow(pid, 60000);
                    await Task.Delay(8000);
                    guard.Dispose();
                    guard = null;
                    ApplyFpsCap(log);
                    log(who + "ran with its own FPS cap (" + a.FpsCap + "); the global FPS setting is active again.");
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("LaunchAsync", ex);
                log(who + "unexpected error: " + ex.Message);
                if (guard != null) { guard.Dispose(); ApplyFpsCap(log); }
                return false;
            }
        }

        // ---------- session / alert state for the watcher ----------
        static bool farmWasRunning;                 // at least one watched window was alive on the last tick
        static DateTime farmStarted = DateTime.Now;
        static int farmRelaunches;                  // auto-relaunches during the current session
        static DateTime lastGuardAlert = DateTime.MinValue;
        static readonly Dictionary<int, DateTime> hungSince = new Dictionary<int, DateTime>();
        public static int DailyRelaunches;          // auto-relaunches since the last daily report

        public static int LatestPid(Account a)
        {
            lock (InstancesLock)
            {
                Instance i = Instances.LastOrDefault(x => x.Account == a);
                return i == null ? 0 : i.Pid;
            }
        }

        // Called after this app closed an account's window on purpose: give the remaining
        // windows a quick memory trim so the freed RAM is visible at once (option, default off).
        public static void TurboAfterClose()
        {
            if (!Store.Settings.TurboClose) return;
            try
            {
                List<int> pids = RobloxPids();
                if (pids.Count > 0) MemClean.TrimProcesses(pids);
            }
            catch (Exception) { }
        }

        // Post-launch mini sequence (Edit account): minimize the window and/or trim its memory.
        public static async Task RunSequence(Account a, int pid, Action<string> log)
        {
            if ((!a.SeqMinimize && !a.SeqTrim) || pid <= 0) return;
            try
            {
                await Task.Delay(12000);                       // let the window come up properly first
                if (!IsAlive(pid)) return;
                if (a.SeqMinimize)
                    foreach (WinInfo w in WindowLayout.FindRobloxWindows())
                        if (w.Pid == pid && w.HasWindow) WindowLayout.Minimize(w.Hwnd);
                if (a.SeqTrim) MemClean.TrimProcesses(new int[] { pid });
                log("[" + AppState.Name(a) + "] sequence done" + (a.SeqMinimize ? " (minimized)" : "") + (a.SeqTrim ? " (memory trimmed)" : "") + ".");
            }
            catch (Exception ex) { Log.Error("RunSequence", ex); }
        }

        // The free-RAM guard refused a launch. Alert, but at most once a minute, so a whole
        // "Launch all" on a full memory bank does not flood the channel.
        static void GuardAlert(long avail, int limitMb)
        {
            if (!Store.Settings.WebhookGuard) return;
            if ((DateTime.Now - lastGuardAlert).TotalSeconds < 60) return;
            lastGuardAlert = DateTime.Now;
            Webhook.SendCritical("Launch blocked", "Not enough free RAM to start another account.", Webhook.Yellow,
                new string[] { "Free RAM", Util.FormatBytes(avail), "Limit", limitMb + " MB" });
        }

        // ---------- watcher: relaunch accounts whose Roblox window disappeared ----------
        public static async Task WatcherTick(Action<string> log)
        {
            List<Instance> snap = SnapshotInstances();
            int aliveCount = 0;
            foreach (Instance i in snap) if (IsAlive(i.Pid)) aliveCount++;
            if (aliveCount > 0)
            {
                if (!farmWasRunning) { farmWasRunning = true; farmStarted = DateTime.Now; farmRelaunches = 0; }
            }
            else if (farmWasRunning)
            {
                farmWasRunning = false;
                if (Store.Settings.WebhookFarm)
                {
                    TimeSpan up = DateTime.Now - farmStarted;
                    string dur = up.TotalHours >= 1
                        ? string.Format("{0}h {1}m", (int)up.TotalHours, up.Minutes)
                        : string.Format("{0}m {1}s", (int)up.TotalMinutes, up.Seconds);
                    Webhook.SendFields("Farm finished", "Every Roblox window this app was watching is closed now.", Webhook.Green,
                        new string[] { "Session length", dur, "Auto-relaunches", farmRelaunches.ToString() });
                }
            }

            if (Store.Settings.GhostMin > 0)
            {
                Dictionary<int, IntPtr> wins = new Dictionary<int, IntPtr>();
                foreach (WinInfo w in WindowLayout.FindRobloxWindows())
                    if (w.HasWindow) wins[w.Pid] = w.Hwnd;
                foreach (Instance i in SnapshotInstances())
                {
                    if (i.UserClosed || !IsAlive(i.Pid)) { hungSince.Remove(i.Pid); continue; }
                    IntPtr hwnd;
                    if (!wins.TryGetValue(i.Pid, out hwnd) || !Native.IsHungAppWindow(hwnd)) { hungSince.Remove(i.Pid); continue; }
                    DateTime since;
                    if (!hungSince.TryGetValue(i.Pid, out since)) { hungSince[i.Pid] = DateTime.Now; continue; }
                    if ((DateTime.Now - since).TotalMinutes >= Store.Settings.GhostMin)
                    {
                        hungSince.Remove(i.Pid);
                        log("Watcher: " + AppState.Name(i.Account) + " has not responded for " + Store.Settings.GhostMin + " min - closing the hung window.");
                        Audit.Write("GHOST CLOSE " + AppState.Name(i.Account));
                        KillPid(i.Pid);
                    }
                }
            }

            if (Store.Settings.WebhookCrash)
                foreach (Instance g in SnapshotInstances().Where(i => !i.UserClosed && !IsAlive(i.Pid)))
                    Webhook.Send("Client closed", AppState.Name(g.Account) + ": its Roblox window disappeared unexpectedly.", Webhook.Red);

            if (!Store.Settings.AutoRelaunch)
            {
                lock (InstancesLock) { Instances.RemoveAll(i => !IsAlive(i.Pid)); }
                return;
            }

            List<Instance> dead = SnapshotInstances().Where(i => !i.UserClosed && !IsAlive(i.Pid)).ToList();

            if (Store.Settings.AutoReconnectKick)
            {
                Dictionary<int, string> titles = new Dictionary<int, string>();
                foreach (WinInfo w in WindowLayout.FindRobloxWindows())
                    titles[w.Pid] = w.Title ?? "";
                foreach (Instance i in SnapshotInstances())
                {
                    if (i.UserClosed || !IsAlive(i.Pid)) continue;
                    string wt;
                    if (titles.TryGetValue(i.Pid, out wt) &&
                        (wt.IndexOf("Crash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         wt.IndexOf("Disconnected", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        log("Auto-Reconnect: wykryto okno bledu/rozlaczenia dla " + AppState.Name(i.Account) + " - restart sesji.");
                        KillPid(i.Pid);
                        if (!dead.Contains(i)) dead.Add(i);
                    }
                }
            }

            if (Store.Settings.WatcherUsePresence || Store.Settings.AutoReconnectKick)
            {
                int graceSec = Math.Max(60, Store.Settings.ReconnectIdleSec);
                foreach (Instance i in SnapshotInstances())
                {
                    if (i.UserClosed || !IsAlive(i.Pid) || i.Account.UserId <= 0) continue;
                    if ((DateTime.Now - i.Started).TotalSeconds < graceSec) continue;
                    List<long> ids = new List<long>();
                    ids.Add(i.Account.UserId);
                    Dictionary<long, PresenceInfo> pr = await Api.Presence(i.Account.Cookie, ids);
                    PresenceInfo pi;
                    if (!pr.TryGetValue(i.Account.UserId, out pi)) continue;
                    AppState.Live(i.Account).PresenceType = pi.Type;
                    if (pi.Type == 2) { i.Strikes = 0; continue; }
                    i.Strikes++;
                    if (i.Strikes >= 2)
                    {
                        log("Auto-Reconnect: " + AppState.Name(i.Account) + " nie jest juz InGame (kick/277) - ponowne dolaczenie.");
                        KillPid(i.Pid);
                        if (!dead.Contains(i)) dead.Add(i);
                    }
                }
            }

            if (dead.Count == 0) return;
            if (!await Api.InternetOk()) { log("Watcher: no internet connection, will retry."); return; }

            foreach (Instance d in dead)
            {
                lock (InstancesLock) { Instances.Remove(d); }
                if (!BreakerAllows(d.Account))
                {
                    log("Watcher: " + AppState.Name(d.Account) + " keeps crashing - the crash breaker stopped relaunching it. Launch it yourself to try again.");
                    continue;
                }
                log("Watcher: relaunching " + AppState.Name(d.Account) + "...");
                await LaunchAsync(d.Account, d.Spec, log);
                bool gaveUp = BreakerRecord(d.Account);
                if (Store.Settings.WebhookRelaunch)
                    Webhook.Send("Client relaunched", AppState.Name(d.Account) + " was brought back by the watcher.", Webhook.Yellow);
                farmRelaunches++;
                DailyRelaunches++;
                if (gaveUp)
                {
                    log("Watcher: " + AppState.Name(d.Account) + " has crashed too often - the crash breaker will stop relaunching it for " + Math.Max(1, Store.Settings.CrashBreakerCooldownMin) + " min.");
                    if (Store.Settings.WebhookCrash)
                        Webhook.SendCritical("Crash breaker", AppState.Name(d.Account) + " crashed too many times - auto-relaunch paused for " + Math.Max(1, Store.Settings.CrashBreakerCooldownMin) + " min.", Webhook.Red,
                            new string[] { "Account", AppState.Name(d.Account), "Paused", Math.Max(1, Store.Settings.CrashBreakerCooldownMin) + " min" });
                }
                await Task.Delay(Math.Max(1, Store.Settings.LaunchDelaySec) * 1000);
            }
        }
    }

    // While a client with its own FPS cap is starting, Roblox may rewrite its settings file (for example a window that
    // is still closing). This watches the file and puts the wanted value straight back.
    public class FpsGuard : IDisposable
    {
        readonly int want;
        readonly Action<string> log;
        FileSystemWatcher watcher;
        DateTime last = DateTime.MinValue;
        readonly object gate = new object();

        public FpsGuard(int cap, Action<string> log)
        {
            want = cap;
            this.log = log;
            try
            {
                string file = Launcher.FindBasicSettings();
                if (file == null) return;
                watcher = new FileSystemWatcher(Path.GetDirectoryName(file), Path.GetFileName(file));
                watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
                watcher.Changed += OnChanged;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception) { watcher = null; }
        }

        void OnChanged(object sender, FileSystemEventArgs e)
        {
            lock (gate)
            {
                if ((DateTime.Now - last).TotalMilliseconds < 400) return;
                last = DateTime.Now;
            }
            Thread.Sleep(300);                                    // let the writer finish
            bool changed;
            Launcher.TryWriteCap(e.FullPath, want, out changed);
            if (changed && log != null) log("Roblox rewrote its FPS setting while the window was starting - put " + want + " back.");
        }

        public void Dispose()
        {
            FileSystemWatcher w = watcher;
            watcher = null;
            if (w != null)
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
        }
    }
}
