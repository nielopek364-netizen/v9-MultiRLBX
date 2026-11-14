using System;
using System.Runtime.InteropServices;

namespace MultiRLBX
{
    /// <summary>
    /// Applies Windows 10/11 Desktop Window Manager (DWM) attributes for immersive dark title bars
    /// and rounded window corners on auxiliary WinForms tool windows.
    /// </summary>
    public static class DwmTheme
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        /// <summary>Configures DWM dark mode and rounded corner preference for the given window handle.</summary>
        /// <param name="hwnd">Native Win32 window handle.</param>
        /// <param name="dark"><c>true</c> to enable dark caption styling; <c>false</c> for light mode.</param>
        public static void Apply(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                int useDark = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20, ref useDark, 4); // DWMWA_USE_IMMERSIVE_DARK_MODE
                int corner = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref corner, 4); // DWMWA_WINDOW_CORNER_PREFERENCE
            }
            catch (Exception) { }
        }
    }
}
