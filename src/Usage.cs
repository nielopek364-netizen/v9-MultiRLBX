using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MultiRLBX
{
    /// <summary>Per-process resource utilization sample captured on a telemetry tick.</summary>
    public class UsageSample
    {
        public double Cpu;
        public long Ram;
    }

    /// <summary>
    /// Samples per-process CPU/RAM usage and maintains thread-safe 60-point rolling history
    /// buffers for dashboard sparklines and system telemetry.
    /// </summary>
    public static class UsageMonitor
    {
        static Dictionary<int, long> lastCpu = new Dictionary<int, long>();
        static DateTime lastAt = DateTime.MinValue;
        public static Dictionary<int, UsageSample> Samples = new Dictionary<int, UsageSample>();
        public static double TotalCpu;
        public static long TotalRam;
        public static int Count;
        public static bool CpuAvailable = true;

        public const int HistLen = 60;
        public static readonly object HistLock = new object();
        public static Dictionary<int, List<double>> CpuHist = new Dictionary<int, List<double>>();
        public static Dictionary<int, List<double>> RamHist = new Dictionary<int, List<double>>();
        public static readonly List<double> SystemFreeMbHist = new List<double>();
        public static readonly List<double> RobloxRamMbHist = new List<double>();
        public static readonly List<double> RobloxCpuHist = new List<double>();

        /// <summary>Returns a thread-safe shallow copy of the specified telemetry history list.</summary>
        public static List<double> Snapshot(List<double> src)
        {
            lock (HistLock) { return new List<double>(src); }
        }

        static void Push(List<double> l, double v)
        {
            l.Add(v);
            if (l.Count > HistLen) l.RemoveRange(0, l.Count - HistLen);
        }

        static void Push(Dictionary<int, List<double>> hist, int pid, double v)
        {
            List<double> l;
            if (!hist.TryGetValue(pid, out l)) { l = new List<double>(); hist[pid] = l; }
            Push(l, v);
        }

        /// <summary>Polls running <c>RobloxPlayerBeta</c> processes and updates CPU/RAM metrics and history rings.</summary>
        public static void Update()
        {
            DateTime now = DateTime.UtcNow;
            double elapsed = lastAt == DateTime.MinValue ? 0 : (now - lastAt).TotalSeconds;
            int cores = Math.Max(1, Environment.ProcessorCount);
            Dictionary<int, long> cpuNow = new Dictionary<int, long>();
            Dictionary<int, UsageSample> fresh = new Dictionary<int, UsageSample>();
            double total = 0;
            long ram = 0;
            int count = 0;
            int cpuFailures = 0;

            foreach (Process p in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                try
                {
                    int pid = p.Id;
                    UsageSample s = new UsageSample();
                    s.Ram = p.WorkingSet64;
                    long cpu = Native.CpuTime100ns(pid);
                    if (cpu < 0) cpuFailures++;
                    else
                    {
                        cpuNow[pid] = cpu;
                        long prev;
                        if (elapsed > 0.2 && lastCpu.TryGetValue(pid, out prev))
                        {
                            s.Cpu = ((cpu - prev) / 10000000.0) / elapsed / cores * 100.0;
                            if (s.Cpu < 0) s.Cpu = 0;
                            if (s.Cpu > 100) s.Cpu = 100;
                        }
                    }
                    fresh[pid] = s;
                    total += s.Cpu;
                    ram += s.Ram;
                    count++;
                }
                catch (Exception) { }
                finally { p.Dispose(); }
            }

            CpuAvailable = !(count > 0 && cpuFailures == count);
            lastCpu = cpuNow;
            lastAt = now;
            Samples = fresh;
            TotalCpu = total;
            TotalRam = ram;
            Count = count;

            lock (HistLock)
            {
                foreach (KeyValuePair<int, UsageSample> kv in fresh)
                {
                    Push(CpuHist, kv.Key, kv.Value.Cpu);
                    Push(RamHist, kv.Key, kv.Value.Ram / 1048576.0);
                }
                foreach (int pid in new List<int>(CpuHist.Keys)) if (!fresh.ContainsKey(pid)) CpuHist.Remove(pid);
                foreach (int pid in new List<int>(RamHist.Keys)) if (!fresh.ContainsKey(pid)) RamHist.Remove(pid);

                Push(RobloxRamMbHist, ram / 1048576.0);
                Push(RobloxCpuHist, total);
                int load; long sysTotal, sysAvail;
                if (MemClean.Status(out load, out sysTotal, out sysAvail))
                    Push(SystemFreeMbHist, sysAvail / 1048576.0);
            }
        }
    }
}
