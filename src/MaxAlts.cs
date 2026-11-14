using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MultiRLBX
{
    /// <summary>
    /// Smart Eco ("Max alts") optimizer: throttles unfocused Roblox clients (Idle priority,
    /// Windows Efficiency Mode, CPU core affinity mask, and working-set trimming) while
    /// restoring normal performance for whichever client currently holds foreground focus.
    /// </summary>
    /// <remarks>
    /// State per PID is cached in <c>lastThrottled</c> and <c>lastAffinity</c> to avoid
    /// redundant Win32 <c>OpenProcess</c> / WMI syscalls on every 2-second tick.
    /// </remarks>
    public static class MaxAlts
    {
        const int ProcessPowerThrottling = 4;
        const uint EXECUTION_SPEED = 0x1;
        const uint PROCESS_SET_INFORMATION = 0x0200;
        const uint PROCESS_SET_QUOTA = 0x0100;

        static DateTime lastTrim = DateTime.MinValue;
        static int lastActive = -2;
        static readonly Dictionary<int, bool> lastThrottled = new Dictionary<int, bool>();
        static readonly Dictionary<int, long> lastAffinity = new Dictionary<int, long>();

        /// <summary>Evaluates foreground focus and applies throttling rules across running Roblox PIDs.</summary>
        public static void Tick()
        {
            if (!Store.Settings.MaxAlts) return;

            List<int> pids = Launcher.RobloxPids();
            if (pids.Count == 0)
            {
                lastThrottled.Clear();
                lastAffinity.Clear();
                lastActive = -2;
                return;
            }

            int active = ActiveRobloxPid(pids);
            int cores = Environment.ProcessorCount;
            int reserve = Math.Max(0, Store.Settings.MaxAltsCores);
            if (reserve >= cores) reserve = cores - 1;

            long bgMask = reserve > 0
                ? ((cores >= 63 ? -1L : (1L << cores) - 1) & ~((1L << reserve) - 1))
                : -1L;

            bool trimNow = (DateTime.UtcNow - lastTrim).TotalSeconds >= 20 && WantTrim();
            bool trimmedAny = false;
            long minTrimBytes = (long)Math.Max(0, Store.Settings.MaxAltsTrimMinMb) * 1024L * 1024L;

            for (int idx = 0; idx < pids.Count; idx++)
            {
                int pid = pids[idx];
                bool isActive = pid == active;

                if (isActive)
                {
                    bool wasThrottled;
                    if (!lastThrottled.TryGetValue(pid, out wasThrottled) || wasThrottled)
                    {
                        SetPriority(pid, ProcessPriorityClass.Normal);
                        SetEfficiency(pid, false);
                        lastThrottled[pid] = false;
                    }
                    long prevAff;
                    if (!lastAffinity.TryGetValue(pid, out prevAff) || prevAff != -1L)
                    {
                        SetAffinity(pid, -1L);
                        lastAffinity[pid] = -1L;
                    }
                    continue;
                }

                if (Store.Settings.MaxAltsIdle)
                {
                    bool wasThrottled;
                    if (!lastThrottled.TryGetValue(pid, out wasThrottled) || !wasThrottled)
                    {
                        SetPriority(pid, ProcessPriorityClass.Idle);
                        SetEfficiency(pid, true);
                        lastThrottled[pid] = true;
                    }
                }
                if (reserve > 0)
                {
                    long prevAff;
                    if (!lastAffinity.TryGetValue(pid, out prevAff) || prevAff != bgMask)
                    {
                        SetAffinity(pid, bgMask);
                        lastAffinity[pid] = bgMask;
                    }
                }
                if (Store.Settings.MaxAltsMinimize) MinimizeIfNeeded(pid);

                bool justLostFocus = pid == lastActive;
                if (Store.Settings.MaxAltsTrim && (trimNow || justLostFocus))
                {
                    bool aboveFloor = true;
                    if (minTrimBytes > 0 && !justLostFocus)
                    {
                        UsageSample sample;
                        if (UsageMonitor.Samples.TryGetValue(pid, out sample) && sample.Ram < minTrimBytes)
                            aboveFloor = false;
                    }
                    if (aboveFloor)
                    {
                        TrimWorkingSet(pid);
                        trimmedAny = true;
                    }
                }
            }

            // Prune exited PIDs from state caches
            HashSet<int> alive = new HashSet<int>(pids);
            foreach (int k in new List<int>(lastThrottled.Keys)) if (!alive.Contains(k)) lastThrottled.Remove(k);
            foreach (int k in new List<int>(lastAffinity.Keys)) if (!alive.Contains(k)) lastAffinity.Remove(k);

            if (trimmedAny) lastTrim = DateTime.UtcNow;
            lastActive = active;
        }

        static bool WantTrim()
        {
            if (!Store.Settings.MaxAltsTrimSmart) return true;
            try
            {
                int load; long total, avail;
                if (!MemClean.Status(out load, out total, out avail)) return true;
                return avail < total / 8 || load >= Store.Settings.Mem.Threshold;
            }
            catch (Exception) { return true; }
        }

        /// <summary>Restores Normal priority, disables Efficiency Mode, and clears affinity restrictions for all clients.</summary>
        public static void Reset()
        {
            foreach (int pid in Launcher.RobloxPids())
            {
                SetPriority(pid, ProcessPriorityClass.Normal);
                SetEfficiency(pid, false);
                SetAffinity(pid, -1L);
            }
            lastThrottled.Clear();
            lastAffinity.Clear();
            lastActive = -2;
        }

        /// <summary>Returns the PID of the currently focused Roblox window, or <c>-1</c> if none.</summary>
        public static int ActiveRobloxPid()
        {
            return ActiveRobloxPid(Launcher.RobloxPids());
        }

        static int ActiveRobloxPid(List<int> pids)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return -1;
            uint pid;
            Native.GetWindowThreadProcessId(fg, out pid);
            for (int i = 0; i < pids.Count; i++)
                if (pids[i] == (int)pid) return pids[i];
            return -1;
        }

        static void SetPriority(int pid, ProcessPriorityClass pc)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                    if (p.PriorityClass != pc) p.PriorityClass = pc;
            }
            catch (Exception) { }
        }

        static void SetEfficiency(int pid, bool on)
        {
            IntPtr h = Native.OpenProcess(PROCESS_SET_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return;
            try
            {
                PROCESS_POWER_THROTTLING_STATE s = new PROCESS_POWER_THROTTLING_STATE();
                s.Version = 1;
                s.ControlMask = EXECUTION_SPEED;
                s.StateMask = on ? EXECUTION_SPEED : 0;
                SetProcessInformation(h, ProcessPowerThrottling, ref s, Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)));
            }
            catch (Exception) { }
            finally { Native.CloseHandle(h); }
        }

        static void SetAffinity(int pid, long mask)
        {
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    IntPtr want = new IntPtr(mask);
                    if (p.ProcessorAffinity != want) p.ProcessorAffinity = want;
                }
            }
            catch (Exception) { }
        }

        static void TrimWorkingSet(int pid)
        {
            IntPtr h = Native.OpenProcess(PROCESS_SET_QUOTA, false, pid);
            if (h == IntPtr.Zero) return;
            try { EmptyWorkingSet(h); }
            catch (Exception) { }
            finally { Native.CloseHandle(h); }
        }

        static void MinimizeIfNeeded(int pid)
        {
            try
            {
                using (new DpiScope())
                    foreach (WinInfo w in WindowLayout.FindRobloxWindows())
                        if (w.Pid == pid && w.HasWindow && !w.Minimized) WindowLayout.Minimize(w.Hwnd);
            }
            catch (Exception) { }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessInformation(IntPtr hProcess, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

        [DllImport("psapi.dll")]
        static extern bool EmptyWorkingSet(IntPtr process);
    }
}
