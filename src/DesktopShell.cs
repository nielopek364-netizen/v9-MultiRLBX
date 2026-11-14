using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace MultiRLBX
{
    /// <summary>
    /// Launches the embedded web UI inside an isolated chromeless Microsoft Edge, Google Chrome,
    /// or Brave application window, falling back to the system default browser if unavailable.
    /// </summary>
    public static class DesktopShell
    {
        static string lastUrl = "";

        /// <summary>Opens the given loopback URL in chromeless app mode or falls back to the default browser.</summary>
        /// <param name="url">Authenticated local server URL including the session token query parameter.</param>
        public static void Open(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            lastUrl = url;
            string browser = FindAppBrowser();
            if (!string.IsNullOrEmpty(browser))
            {
                try
                {
                    string profile = Path.Combine(Store.Dir, "ShellProfile");
                    Directory.CreateDirectory(profile);
                    string args = "--app=\"" + url + "\" --window-size=1380,860 --user-data-dir=\"" + profile + "\" --no-first-run --no-default-browser-check";
                    ProcessStartInfo psi = new ProcessStartInfo(browser, args);
                    psi.UseShellExecute = false;
                    Process pr = Process.Start(psi);
                    if (pr != null)
                    {
                        // Short wait detects immediate non-zero exits (e.g., locked/corrupt Chromium profile)
                        if (!pr.WaitForExit(350) || pr.ExitCode == 0) return;
                    }
                }
                catch (Exception ex) { Log.Error("DesktopShell.Open", ex); }
            }

            OpenInDefaultBrowser(url);
        }

        /// <summary>Re-opens the most recently launched shell URL (used by tray and single-instance wake handlers).</summary>
        public static void Reopen()
        {
            if (!string.IsNullOrEmpty(lastUrl)) Open(lastUrl);
        }

        /// <summary>Opens the dashboard URL using the OS default browser handler.</summary>
        /// <param name="url">Target dashboard URL.</param>
        public static void OpenInDefaultBrowser(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            lastUrl = url;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(url);
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex) { Log.Error("DesktopShell.DefaultBrowser", ex); }
        }

        static string FindAppBrowser()
        {
            string pf = Environment.GetEnvironmentVariable("ProgramFiles");
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            string lad = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            List<string> candidates = new List<string>();
            if (pf86 != null) candidates.Add(Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"));
            if (pf != null) candidates.Add(Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"));
            if (lad != null) candidates.Add(Path.Combine(lad, @"Microsoft\Edge\Application\msedge.exe"));
            if (pf != null) candidates.Add(Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"));
            if (pf86 != null) candidates.Add(Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"));
            if (lad != null) candidates.Add(Path.Combine(lad, @"Google\Chrome\Application\chrome.exe"));
            if (pf != null) candidates.Add(Path.Combine(pf, @"BraveSoftware\Brave-Browser\Application\brave.exe"));
            foreach (string p in candidates)
                if (File.Exists(p)) return p;
            return null;
        }
    }
}
