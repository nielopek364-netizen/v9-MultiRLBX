using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MultiRLBX
{
    /// <summary>
    /// Single macro input step. <see cref="T"/> is the cumulative offset in milliseconds from start.
    /// Supports mouse movement, clicks, scroll wheels, keyboard keys, and programmable wait checkpoints.
    /// </summary>
    public class MacroEvent
    {
        public const int Move = 0, Button = 1, Wheel = 2, HWheel = 3, Key = 4, Wait = 5;
        public int T;
        public int Kind;
        public int A;       // Button: 1..5 | Wheel: delta | Key: virtual key | Wait: duration ms
        public int B;       // Button: 1 down, 0 up | Key: scan code
        public int C;       // Key: bit 0 = up, bit 1 = extended key
        public int X;       // Screen X position (Move, Button, Wheel)
        public int Y;       // Screen Y position (Move, Button, Wheel)
    }

    /// <summary>
    /// Represents a custom <c>.v9m</c> v2 macro document including metadata, humanization jitter,
    /// and ordered <see cref="MacroEvent"/> steps.
    /// </summary>
    public class MacroDocument
    {
        public string Name = "Custom Macro";
        public string Author = "v9pv";
        public double Speed = 1.0;
        public int Repeats = 1;
        public int JitterMs;
        public int JitterPx;
        public string FilePath = "";
        public List<MacroEvent> Events = new List<MacroEvent>();

        public int LengthMs
        {
            get { return MacroRecorder.LengthMs(Events); }
        }
    }

    /// <summary>
    /// Low-level Windows input hook recorder and custom <c>.v9m</c> library manager.
    /// Ignores synthetic input injected by other automation or Anti-AFK ticks.
    /// </summary>
    public static class MacroRecorder
    {
        public static bool Recording { get; private set; }
        public static readonly List<MacroEvent> Events = new List<MacroEvent>();
        public static bool RecordMoves = true;
        public static bool HiRes = true;

        public const string FileExt = ".v9m";
        const string HeaderV2 = "#v9m 2";
        const string LegacyHeader1 = "MultiRLBXMacro 1";
        const string LegacyHeader2 = "RobloxMultiHubMacro 1";

        static IntPtr kbHook = IntPtr.Zero;
        static IntPtr msHook = IntPtr.Zero;
        static HookProc kbProc = KeyboardProc;          // kept in fields so GC never collects them
        static HookProc msProc = MouseProc;
        static readonly Stopwatch clock = new Stopwatch();
        static int lastMoveT = -1000;

        /// <summary>Returns the directory inside <c>%APPDATA%\MultiRLBX</c> where custom <c>.v9m</c> macros are stored.</summary>
        public static string LibraryDir
        {
            get
            {
                string dir = Path.Combine(Store.Dir ?? ".", "macros");
                try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch (Exception) { }
                return dir;
            }
        }

        public static bool Start()
        {
            if (Recording) return true;
            Events.Clear();
            lastMoveT = -1000;
            IntPtr mod = GetModuleHandle(null);
            kbHook = SetWindowsHookEx(13, kbProc, mod, 0);   // WH_KEYBOARD_LL
            msHook = SetWindowsHookEx(14, msProc, mod, 0);   // WH_MOUSE_LL
            if (kbHook == IntPtr.Zero || msHook == IntPtr.Zero) { Unhook(); return false; }
            clock.Restart();
            Recording = true;
            return true;
        }

        public static void Stop()
        {
            if (!Recording) return;
            Recording = false;
            Unhook();
            clock.Stop();
            Normalize(Events);
        }

        static void Unhook()
        {
            if (kbHook != IntPtr.Zero) { UnhookWindowsHookEx(kbHook); kbHook = IntPtr.Zero; }
            if (msHook != IntPtr.Zero) { UnhookWindowsHookEx(msHook); msHook = IntPtr.Zero; }
        }

        /// <summary>
        /// Strips orphan key/button releases at the start and dangling presses at the end
        /// so playback never leaves a modifier or button stuck down.
        /// </summary>
        public static void Normalize(List<MacroEvent> ev)
        {
            HashSet<string> down = new HashSet<string>();
            List<MacroEvent> keep = new List<MacroEvent>();
            foreach (MacroEvent e in ev)
            {
                string id = PressId(e);
                if (id == null) { keep.Add(e); continue; }
                bool isDown = e.Kind == MacroEvent.Key ? (e.C & 1) == 0 : e.B == 1;
                if (isDown) { down.Add(id); keep.Add(e); }
                else if (down.Remove(id)) keep.Add(e);
            }
            for (int i = keep.Count - 1; i >= 0 && down.Count > 0; i--)
            {
                string id = PressId(keep[i]);
                if (id == null) continue;
                bool isDown = keep[i].Kind == MacroEvent.Key ? (keep[i].C & 1) == 0 : keep[i].B == 1;
                if (isDown && down.Remove(id)) keep.RemoveAt(i);
            }
            ev.Clear();
            ev.AddRange(keep);
        }

        /// <summary>Shifts all step timestamps so the first step begins at <c>0 ms</c>.</summary>
        public static void TrimIdle(List<MacroEvent> ev)
        {
            if (ev == null || ev.Count == 0) return;
            int offset = ev[0].T;
            if (offset <= 0) return;
            foreach (MacroEvent e in ev)
                e.T = Math.Max(0, e.T - offset);
        }

        /// <summary>Appends a programmable wait delay checkpoint to the end of a custom macro.</summary>
        public static void AddWait(List<MacroEvent> ev, int durationMs)
        {
            if (ev == null) return;
            int dur = Math.Max(10, Math.Min(600000, durationMs));
            int start = LengthMs(ev);
            MacroEvent w = new MacroEvent();
            w.T = start + dur;
            w.Kind = MacroEvent.Wait;
            w.A = dur;
            ev.Add(w);
        }

        /// <summary>Appends a synthetic click step (down + release) at screen coordinates.</summary>
        public static void AddClick(List<MacroEvent> ev, int x, int y, int button, int delayBeforeMs)
        {
            if (ev == null) return;
            int t = LengthMs(ev) + Math.Max(0, delayBeforeMs);
            int btn = button >= 1 && button <= 5 ? button : 1;

            MacroEvent down = new MacroEvent();
            down.T = t; down.Kind = MacroEvent.Button; down.A = btn; down.B = 1; down.X = x; down.Y = y;
            ev.Add(down);

            MacroEvent up = new MacroEvent();
            up.T = t + 35; up.Kind = MacroEvent.Button; up.A = btn; up.B = 0; up.X = x; up.Y = y;
            ev.Add(up);
        }

        /// <summary>Appends a synthetic keyboard press and release pair to a custom macro.</summary>
        public static void AddKey(List<MacroEvent> ev, int vk, int holdMs, int delayBeforeMs)
        {
            if (ev == null) return;
            int t = LengthMs(ev) + Math.Max(0, delayBeforeMs);
            int hold = Math.Max(20, Math.Min(30000, holdMs));

            MacroEvent down = new MacroEvent();
            down.T = t; down.Kind = MacroEvent.Key; down.A = vk; down.B = 0; down.C = 0;
            ev.Add(down);

            MacroEvent up = new MacroEvent();
            up.T = t + hold; up.Kind = MacroEvent.Key; up.A = vk; up.B = 0; up.C = 1;
            ev.Add(up);
        }

        static string PressId(MacroEvent e)
        {
            if (e.Kind == MacroEvent.Key) return "k" + e.A;
            if (e.Kind == MacroEvent.Button) return "b" + e.A;
            return null;
        }

        static IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && Recording)
            {
                KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                if ((k.flags & 0x10) == 0)                  // LLKHF_INJECTED
                {
                    MacroEvent e = new MacroEvent();
                    e.T = (int)clock.ElapsedMilliseconds;
                    e.Kind = MacroEvent.Key;
                    e.A = (int)k.vkCode;
                    e.B = (int)k.scanCode;
                    e.C = ((k.flags & 0x80) != 0 ? 1 : 0) | ((k.flags & 0x01) != 0 ? 2 : 0);
                    Events.Add(e);
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        static IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && Recording)
            {
                MSLLHOOKSTRUCT m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                if ((m.flags & 0x01) == 0)                  // LLMHF_INJECTED
                {
                    int msg = wParam.ToInt32();
                    MacroEvent e = new MacroEvent();
                    e.T = (int)clock.ElapsedMilliseconds;
                    e.X = m.x;
                    e.Y = m.y;
                    short hi = (short)((m.mouseData >> 16) & 0xFFFF);
                    bool add = true;
                    switch (msg)
                    {
                        case 0x0200:
                            e.Kind = MacroEvent.Move;
                            add = RecordMoves && (HiRes || e.T - lastMoveT >= 15);
                            if (add) lastMoveT = e.T;
                            break;
                        case 0x0201: e.Kind = MacroEvent.Button; e.A = 1; e.B = 1; break;
                        case 0x0202: e.Kind = MacroEvent.Button; e.A = 1; e.B = 0; break;
                        case 0x0204: e.Kind = MacroEvent.Button; e.A = 2; e.B = 1; break;
                        case 0x0205: e.Kind = MacroEvent.Button; e.A = 2; e.B = 0; break;
                        case 0x0207: e.Kind = MacroEvent.Button; e.A = 3; e.B = 1; break;
                        case 0x0208: e.Kind = MacroEvent.Button; e.A = 3; e.B = 0; break;
                        case 0x020B: e.Kind = MacroEvent.Button; e.A = hi == 2 ? 5 : 4; e.B = 1; break;
                        case 0x020C: e.Kind = MacroEvent.Button; e.A = hi == 2 ? 5 : 4; e.B = 0; break;
                        case 0x020A: e.Kind = MacroEvent.Wheel; e.A = hi; break;
                        case 0x020E: e.Kind = MacroEvent.HWheel; e.A = hi; break;
                        default: add = false; break;
                    }
                    if (add) Events.Add(e);
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        // ---------------- Custom .v9m format v2 (with legacy reader fallback) ----------------

        /// <summary>Writes events in the custom <c>.v9m</c> v2 format.</summary>
        public static void Save(string path, List<MacroEvent> ev)
        {
            MacroDocument doc = new MacroDocument();
            doc.Name = Path.GetFileNameWithoutExtension(path);
            doc.Author = "v9pv";
            doc.Speed = Store.Settings.Macro != null ? Store.Settings.Macro.Speed : 1.0;
            doc.Repeats = Store.Settings.Macro != null ? Store.Settings.Macro.Repeats : 1;
            doc.JitterMs = Store.Settings.Macro != null ? Store.Settings.Macro.JitterMs : 0;
            doc.JitterPx = Store.Settings.Macro != null ? Store.Settings.Macro.JitterPx : 0;
            doc.Events = ev ?? new List<MacroEvent>();
            SaveV9m(path, doc);
        }

        /// <summary>Serializes a full <see cref="MacroDocument"/> with custom metadata and steps to <c>.v9m</c>.</summary>
        public static void SaveV9m(string path, MacroDocument doc)
        {
            if (doc == null) return;
            List<MacroEvent> ev = doc.Events ?? new List<MacroEvent>();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(HeaderV2);
            sb.AppendLine("#name=" + CleanMeta(string.IsNullOrEmpty(doc.Name) ? Path.GetFileNameWithoutExtension(path) : doc.Name));
            sb.AppendLine("#author=" + CleanMeta(string.IsNullOrEmpty(doc.Author) ? "v9pv" : doc.Author));
            sb.AppendLine("#speed=" + doc.Speed.ToString("0.###", CultureInfo.InvariantCulture));
            sb.AppendLine("#repeats=" + Math.Max(1, doc.Repeats).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("#jitterMs=" + Math.Max(0, doc.JitterMs).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("#jitterPx=" + Math.Max(0, doc.JitterPx).ToString(CultureInfo.InvariantCulture));
            sb.AppendLine("#steps=" + ev.Count.ToString(CultureInfo.InvariantCulture));
            foreach (MacroEvent e in ev)
                sb.Append(e.T).Append(' ').Append(e.Kind).Append(' ').Append(e.A).Append(' ').Append(e.B).Append(' ')
                  .Append(e.C).Append(' ').Append(e.X).Append(' ').Append(e.Y).Append("\r\n");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        static string CleanMeta(string s)
        {
            return (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        }

        /// <summary>Loads events from a custom <c>.v9m</c> file (or legacy v1 macro file).</summary>
        public static List<MacroEvent> Load(string path)
        {
            return LoadV9m(path).Events;
        }

        /// <summary>Reads a <see cref="MacroDocument"/> from disk, parsing <c>#v9m 2</c> headers and steps.</summary>
        public static MacroDocument LoadV9m(string path)
        {
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            if (lines.Length == 0)
                throw new InvalidDataException("Empty macro file.");

            string first = lines[0].Trim();
            bool isV2 = first == HeaderV2 || first.StartsWith("#v9m ", StringComparison.OrdinalIgnoreCase);
            bool isLegacy = first == LegacyHeader1 || first == LegacyHeader2;
            if (!isV2 && !isLegacy)
                throw new InvalidDataException("Not a valid v9 MultiRLBX (.v9m) macro file.");

            MacroDocument doc = new MacroDocument();
            doc.FilePath = path;
            doc.Name = Path.GetFileNameWithoutExtension(path);

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line[0] == '#')
                {
                    int eq = line.IndexOf('=');
                    if (eq > 1)
                    {
                        string k = line.Substring(1, eq - 1).Trim().ToLowerInvariant();
                        string v = line.Substring(eq + 1).Trim();
                        double dVal;
                        int iVal;
                        if (k == "name" && v.Length > 0) doc.Name = v;
                        else if (k == "author" && v.Length > 0) doc.Author = v;
                        else if (k == "speed" && double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out dVal)) doc.Speed = dVal;
                        else if (k == "repeats" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out iVal)) doc.Repeats = Math.Max(1, iVal);
                        else if (k == "jitterms" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out iVal)) doc.JitterMs = Math.Max(0, iVal);
                        else if (k == "jitterpx" && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out iVal)) doc.JitterPx = Math.Max(0, iVal);
                    }
                    continue;
                }

                string[] p = line.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 7) continue;
                MacroEvent e = new MacroEvent();
                e.T = int.Parse(p[0], CultureInfo.InvariantCulture);
                e.Kind = int.Parse(p[1], CultureInfo.InvariantCulture);
                e.A = int.Parse(p[2], CultureInfo.InvariantCulture);
                e.B = int.Parse(p[3], CultureInfo.InvariantCulture);
                e.C = int.Parse(p[4], CultureInfo.InvariantCulture);
                e.X = int.Parse(p[5], CultureInfo.InvariantCulture);
                e.Y = int.Parse(p[6], CultureInfo.InvariantCulture);
                doc.Events.Add(e);
            }
            return doc;
        }

        /// <summary>Enumerates all saved <c>.v9m</c> macros in <see cref="LibraryDir"/>.</summary>
        public static List<MacroDocument> ListLibrary()
        {
            List<MacroDocument> list = new List<MacroDocument>();
            try
            {
                string dir = LibraryDir;
                if (!Directory.Exists(dir)) return list;
                foreach (string f in Directory.GetFiles(dir, "*" + FileExt))
                {
                    try { list.Add(LoadV9m(f)); }
                    catch (Exception) { }
                }
            }
            catch (Exception ex) { Log.Error("MacroRecorder.ListLibrary", ex); }
            return list;
        }

        /// <summary>Builds a sanitized <c>.v9m</c> path inside <see cref="LibraryDir"/>.</summary>
        public static string LibraryPathFor(string name)
        {
            string clean = (name ?? "macro").Trim();
            foreach (char ch in Path.GetInvalidFileNameChars()) clean = clean.Replace(ch, '_');
            if (clean.EndsWith(FileExt, StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(0, clean.Length - FileExt.Length);
            if (string.IsNullOrWhiteSpace(clean)) clean = "custom_macro";
            return Path.Combine(LibraryDir, clean + FileExt);
        }

        public static int LengthMs(List<MacroEvent> ev) { return ev == null || ev.Count == 0 ? 0 : ev[ev.Count - 1].T; }

        // ---------------- native ----------------
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr extra; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT { public int x; public int y; public uint mouseData; public uint flags; public uint time; public IntPtr extra; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr mod, uint threadId);

        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hook);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);
    }

    /// <summary>
    /// Threaded <c>SendInput</c> macro playback engine supporting variable playback speed,
    /// custom <c>.v9m</c> wait steps, timing/pixel jitter humanization, and target window focusing.
    /// </summary>
    public class MacroPlayer
    {
        public double Speed = 1.0;
        public int Repeats = 1;                 // ignored when Continuous
        public bool Continuous;
        public int PauseMs;                     // pause between two repeats
        public int JitterMs;                    // randomized +/- timing variance per step (ms)
        public int JitterPx;                    // randomized +/- mouse pixel variance
        public string TargetAccountId = "";     // optional Roblox account window to focus before start
        public string ActiveName = "";
        public event Action<int> RepeatStarted; // 1-based
        public event Action Finished;

        volatile bool stop;
        Thread thread;
        readonly HashSet<int> keysDown = new HashSet<int>();
        readonly HashSet<int> buttonsDown = new HashSet<int>();
        readonly Random rng = new Random();

        public bool Playing { get { return thread != null && thread.IsAlive; } }

        public void Start(List<MacroEvent> events)
        {
            if (Playing || events == null || events.Count == 0) return;
            List<MacroEvent> copy = events.ToList();
            stop = false;
            thread = new Thread(delegate() { Run(copy); });
            thread.IsBackground = true;
            thread.Priority = ThreadPriority.AboveNormal;
            thread.Start();
        }

        public void Stop() { stop = true; }

        void FocusTargetWindowIfConfigured()
        {
            if (string.IsNullOrEmpty(TargetAccountId)) return;
            try
            {
                Instance inst = Launcher.SnapshotInstances().FirstOrDefault(x => x.Account != null && x.Account.Id == TargetAccountId && Launcher.IsAlive(x.Pid));
                if (inst == null) return;
                WinInfo w = WindowLayout.FindRobloxWindows().FirstOrDefault(x => x.Pid == inst.Pid && x.HasWindow);
                if (w != null)
                {
                    WindowLayout.Focus(w.Hwnd);
                    Thread.Sleep(180);
                }
            }
            catch (Exception ex) { Log.Error("MacroPlayer.Focus", ex); }
        }

        void Run(List<MacroEvent> ev)
        {
            timeBeginPeriod(1);
            try
            {
                FocusTargetWindowIfConfigured();
                double speed = Math.Max(0.01, Speed);
                for (int rep = 1; Continuous || rep <= Repeats; rep++)
                {
                    if (stop) break;
                    if (RepeatStarted != null) RepeatStarted(rep);
                    Stopwatch sw = Stopwatch.StartNew();
                    foreach (MacroEvent e in ev)
                    {
                        double targetMs = e.T / speed;
                        if (JitterMs > 0) targetMs = Math.Max(0, targetMs + rng.Next(-JitterMs, JitterMs + 1));
                        if (!WaitUntil(sw, targetMs)) break;
                        Send(e);
                    }
                    ReleaseAll();
                    if (stop) break;
                    if ((Continuous || rep < Repeats) && PauseMs > 0 && !WaitUntil(Stopwatch.StartNew(), PauseMs)) break;
                }
            }
            catch (Exception ex) { Log.Error("MacroPlayer", ex); }
            finally
            {
                ReleaseAll();
                timeEndPeriod(1);
                ActiveName = "";
                if (Finished != null) Finished();
            }
        }

        bool WaitUntil(Stopwatch sw, double ms)
        {
            while (true)
            {
                if (stop) return false;
                double left = ms - sw.Elapsed.TotalMilliseconds;
                if (left <= 0) return true;
                if (left > 3) Thread.Sleep((int)Math.Min(50, left - 2));
                else Thread.Yield();
            }
        }

        int JitterCoord(int val)
        {
            return JitterPx > 0 ? val + rng.Next(-JitterPx, JitterPx + 1) : val;
        }

        void Send(MacroEvent e)
        {
            if (e.Kind == MacroEvent.Wait) return;
            if (e.Kind == MacroEvent.Key)
            {
                bool up = (e.C & 1) != 0;
                uint flags = (up ? 0x0002u : 0u) | ((e.C & 2) != 0 ? 0x0001u : 0u);
                SendKey((ushort)e.A, (ushort)e.B, flags);
                if (up) keysDown.Remove(e.A); else keysDown.Add(e.A);
                return;
            }
            if (e.Kind == MacroEvent.Move) { SendMouse(JitterCoord(e.X), JitterCoord(e.Y), 0x0001, 0); return; }
            if (e.Kind == MacroEvent.Wheel) { SendMouse(e.X, e.Y, 0x0001 | 0x0800, e.A); return; }
            if (e.Kind == MacroEvent.HWheel) { SendMouse(e.X, e.Y, 0x0001 | 0x01000, e.A); return; }
            if (e.Kind == MacroEvent.Button)
            {
                bool down = e.B == 1;
                uint f;
                int data = 0;
                switch (e.A)
                {
                    case 1: f = down ? 0x0002u : 0x0004u; break;
                    case 2: f = down ? 0x0008u : 0x0010u; break;
                    case 3: f = down ? 0x0020u : 0x0040u; break;
                    default: f = down ? 0x0080u : 0x0100u; data = e.A == 5 ? 2 : 1; break;
                }
                SendMouse(JitterCoord(e.X), JitterCoord(e.Y), 0x0001 | f, data);
                if (down) buttonsDown.Add(e.A); else buttonsDown.Remove(e.A);
            }
        }

        void ReleaseAll()
        {
            foreach (int vk in keysDown.ToList()) SendKey((ushort)vk, 0, 0x0002);
            keysDown.Clear();
            foreach (int b in buttonsDown.ToList())
            {
                uint f = b == 1 ? 0x0004u : b == 2 ? 0x0010u : b == 3 ? 0x0040u : 0x0100u;
                SendInputs(MouseInput(0, 0, f, b == 5 ? 2 : (b == 4 ? 1 : 0)));
            }
            buttonsDown.Clear();
        }

        static void SendKey(ushort vk, ushort scan, uint flags)
        {
            INPUT i = new INPUT();
            i.type = 1;
            i.u.ki.wVk = vk;
            i.u.ki.wScan = scan;
            i.u.ki.dwFlags = flags;
            SendInputs(i);
        }

        static void SendMouse(int x, int y, uint flags, int data)
        {
            int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);
            int vw = Math.Max(2, GetSystemMetrics(78)), vh = Math.Max(2, GetSystemMetrics(79));
            int ax = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
            int ay = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
            SendInputs(MouseInput(ax, ay, flags | 0x8000 | 0x4000, data));
        }

        static INPUT MouseInput(int dx, int dy, uint flags, int data)
        {
            INPUT i = new INPUT();
            i.type = 0;
            i.u.mi.dx = dx;
            i.u.mi.dy = dy;
            i.u.mi.mouseData = unchecked((uint)data);
            i.u.mi.dwFlags = flags;
            return i;
        }

        static void SendInputs(INPUT i)
        {
            SendInput(1, new INPUT[] { i }, Marshal.SizeOf(typeof(INPUT)));
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion u; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);

        [DllImport("winmm.dll")]
        static extern uint timeBeginPeriod(uint ms);

        [DllImport("winmm.dll")]
        static extern uint timeEndPeriod(uint ms);
    }
}
