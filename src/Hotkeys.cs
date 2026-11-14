using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MultiRLBX
{
    /// <summary>
    /// Manages system-wide Windows hotkeys outside of macro playback via a hidden native message window:
    /// Ctrl+Shift+T (Auto-Tile windows), Ctrl+Shift+M (Clean RAM), Ctrl+Shift+K (Panic Stop All).
    /// </summary>
    public static class Hotkeys
    {
        const uint MOD_CONTROL = 0x0002;
        const uint MOD_SHIFT = 0x0004;
        const uint MOD_NOREPEAT = 0x4000;
        const int WM_HOTKEY = 0x0312;

        const int HK_TILE = 9101;
        const int HK_RAM = 9102;
        const int HK_STOP = 9103;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        sealed class HotkeyWindow : NativeWindow, IDisposable
        {
            public HotkeyWindow()
            {
                CreateHandle(new CreateParams());
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY)
                {
                    int id = m.WParam.ToInt32();
                    if (id == HK_TILE)
                    {
                        AppEngine.AddLog("Hotkey Ctrl+Shift+T: ukladanie okien.");
                        AppEngine.ApplyLayout();
                    }
                    else if (id == HK_RAM)
                    {
                        AppEngine.AddLog("Hotkey Ctrl+Shift+M: czyszczenie RAM.");
                        Task.Run(async delegate { await AppEngine.CleanMemoryAsync(); });
                    }
                    else if (id == HK_STOP)
                    {
                        AppEngine.AddLog("Hotkey Ctrl+Shift+K: Panic Stop All.");
                        AppEngine.StopAll();
                    }
                }
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                try { if (Handle != IntPtr.Zero) DestroyHandle(); } catch (Exception) { }
            }
        }

        static HotkeyWindow window;
        static bool active;

        /// <summary>Enables or disables the 3 global management hotkeys on the STA UI thread.</summary>
        public static void SetEnabled(bool enabled)
        {
            AppEngine.RunOnUi(delegate
            {
                if (enabled) RegisterAll();
                else UnregisterAll();
            });
        }

        static void RegisterAll()
        {
            if (active) return;
            try
            {
                if (window == null) window = new HotkeyWindow();
                uint mods = MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT;
                RegisterHotKey(window.Handle, HK_TILE, mods, (uint)Keys.T);
                RegisterHotKey(window.Handle, HK_RAM, mods, (uint)Keys.M);
                RegisterHotKey(window.Handle, HK_STOP, mods, (uint)Keys.K);
                active = true;
            }
            catch (Exception ex) { Log.Error("Hotkeys.RegisterAll", ex); }
        }

        static void UnregisterAll()
        {
            if (window == null) return;
            try
            {
                if (window.Handle != IntPtr.Zero)
                {
                    UnregisterHotKey(window.Handle, HK_TILE);
                    UnregisterHotKey(window.Handle, HK_RAM);
                    UnregisterHotKey(window.Handle, HK_STOP);
                }
            }
            catch (Exception) { }
            active = false;
        }

        /// <summary>Unregisters all global hotkeys and destroys the hidden message window on shutdown.</summary>
        public static void Dispose()
        {
            UnregisterAll();
            if (window != null)
            {
                window.Dispose();
                window = null;
            }
        }
    }
}
