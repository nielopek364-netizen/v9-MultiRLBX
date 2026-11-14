using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultiRLBX
{
    public class WebLoginStatus
    {
        public bool Active;
        public bool Completed;
        public bool Cancelled;
        public string Browser = "";
        public string Username = "";
        public string Error = "";
    }

    /// <summary>
    /// Launches an isolated Chromium app window (Edge, Chrome, or Brave) on a temporary profile
    /// and reads <c>.ROBLOSECURITY</c> over the local loopback DevTools WebSocket once login finishes.
    /// </summary>
    public static class WebAuth
    {
        static readonly object gate = new object();
        static CancellationTokenSource cts;
        static Process activeProc;
        static string activeProfileDir = "";
        static WebLoginStatus current = new WebLoginStatus();

        public static WebLoginStatus GetStatus()
        {
            lock (gate)
            {
                WebLoginStatus copy = new WebLoginStatus();
                copy.Active = current.Active;
                copy.Completed = current.Completed;
                copy.Cancelled = current.Cancelled;
                copy.Browser = current.Browser;
                copy.Username = current.Username;
                copy.Error = current.Error;
                return copy;
            }
        }

        public static void Cancel()
        {
            CancellationTokenSource oldCts;
            Process proc;
            string dir;
            lock (gate)
            {
                oldCts = cts;
                cts = null;
                proc = activeProc;
                activeProc = null;
                dir = activeProfileDir;
                activeProfileDir = "";
                if (current.Active)
                {
                    current.Active = false;
                    current.Cancelled = true;
                    current.Error = "Anulowano logowanie przez przegladarke.";
                }
            }
            try { if (oldCts != null) oldCts.Cancel(); } catch (Exception) { }
            try { if (proc != null && !proc.HasExited) proc.Kill(); } catch (Exception) { }
            CleanupDirAsync(dir);
        }

        public static WebLoginStatus Start(string targetUrl, string group, string alias, string browserPref)
        {
            Cancel();

            string browserName;
            string browserExe = FindBrowser(browserPref, out browserName);
            if (string.IsNullOrEmpty(browserExe))
            {
                WebLoginStatus err = new WebLoginStatus();
                err.Error = "Nie znaleziono przegladarki Edge, Chrome ani Brave na tym komputerze.";
                return err;
            }

            string url = SanitizeTargetUrl(targetUrl);
            int port = AllocateLoopbackPort();
            string profileDir = Path.Combine(Path.GetTempPath(), "MultiRLBX_WebAuth_" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(profileDir); }
            catch (Exception ex)
            {
                WebLoginStatus err = new WebLoginStatus();
                err.Error = "Nie mozna utworzyc tymczasowego profilu: " + ex.Message;
                return err;
            }

            string args = "--app=\"" + url + "\" --window-size=540,740 --user-data-dir=\"" + profileDir +
                "\" --remote-debugging-port=" + port +
                " --no-first-run --no-default-browser-check --disable-extensions";

            Process proc;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(browserExe, args);
                psi.UseShellExecute = false;
                proc = Process.Start(psi);
                if (proc == null) throw new InvalidOperationException("Browser failed to start.");
            }
            catch (Exception ex)
            {
                CleanupDirAsync(profileDir);
                WebLoginStatus err = new WebLoginStatus();
                err.Error = "Blad uruchomienia przegladarki: " + ex.Message;
                return err;
            }

            CancellationTokenSource localCts = new CancellationTokenSource();
            lock (gate)
            {
                cts = localCts;
                activeProc = proc;
                activeProfileDir = profileDir;
                current = new WebLoginStatus();
                current.Active = true;
                current.Browser = browserName;
            }

            CancellationToken token = localCts.Token;
            Task.Run(async delegate
            {
                await WatchLoginAsync(proc, port, profileDir, group ?? "", alias ?? "", token);
            });

            return GetStatus();
        }

        static async Task WatchLoginAsync(Process proc, int port, string profileDir, string group, string alias, CancellationToken ct)
        {
            try
            {
                using (HttpClient http = new HttpClient())
                {
                    http.Timeout = TimeSpan.FromSeconds(3);
                    DateTime deadline = DateTime.UtcNow.AddMinutes(8);

                    while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
                    {
                        if (proc.HasExited)
                        {
                            lock (gate)
                            {
                                if (current.Active)
                                {
                                    current.Active = false;
                                    current.Cancelled = true;
                                    current.Error = "Okno przegladarki zostalo zamkniete przed zakonczeniem logowania.";
                                }
                            }
                            return;
                        }

                        string wsUrl = await DiscoverWebSocketUrlAsync(http, port, ct);
                        if (!string.IsNullOrEmpty(wsUrl))
                        {
                            string cookie = await ReadRoblosecurityViaCdpAsync(wsUrl, ct);
                            if (!string.IsNullOrEmpty(cookie) && cookie.Length >= 40)
                            {
                                UserInfo u = await Api.Authenticated(cookie);
                                if (u.Valid)
                                {
                                    SaveVerifiedAccount(u, cookie, group, alias);
                                    lock (gate)
                                    {
                                        current.Active = false;
                                        current.Completed = true;
                                        current.Username = u.Name;
                                        current.Error = "";
                                    }
                                    try { if (!proc.HasExited) proc.Kill(); } catch (Exception) { }
                                    return;
                                }
                            }
                        }

                        await Task.Delay(1200, ct);
                    }

                    if (!ct.IsCancellationRequested)
                    {
                        lock (gate)
                        {
                            if (current.Active)
                            {
                                current.Active = false;
                                current.Error = "Przekroczono limit czasu logowania (8 min).";
                            }
                        }
                        try { if (!proc.HasExited) proc.Kill(); } catch (Exception) { }
                    }
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Log.Error("WebAuth.WatchLogin", ex);
                lock (gate)
                {
                    if (current.Active)
                    {
                        current.Active = false;
                        current.Error = "Blad sesji logowania: " + ex.Message;
                    }
                }
            }
            finally
            {
                lock (gate)
                {
                    if (activeProc == proc) activeProc = null;
                    if (activeProfileDir == profileDir) activeProfileDir = "";
                }
                CleanupDirAsync(profileDir);
            }
        }

        static void SaveVerifiedAccount(UserInfo u, string cookie, string group, string alias)
        {
            Account existing = Store.Accounts.FirstOrDefault(x => x.UserId == u.Id || x.Username.Equals(u.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Cookie = cookie;
                existing.Username = u.Name;
                existing.DisplayName = u.Display;
                existing.LoginLost = false;
                if (!string.IsNullOrEmpty(group)) existing.Group = group;
                if (!string.IsNullOrEmpty(alias)) existing.Alias = alias;
            }
            else
            {
                Account a = new Account();
                a.Username = u.Name;
                a.DisplayName = u.Display;
                a.UserId = u.Id;
                a.Cookie = cookie;
                a.Group = group;
                a.Alias = alias;
                a.Order = Store.Accounts.Count;
                Store.Accounts.Add(a);
            }
            if (!string.IsNullOrEmpty(group) && !Store.Settings.Groups.Contains(group))
            {
                Store.Settings.Groups.Add(group);
                Store.SaveSettings();
            }
            Store.SaveAccounts();
            AppEngine.AddLog("Web Login: dodano/odswiezono konto " + u.Name + " (" + u.Id + ").");
        }

        static async Task<string> DiscoverWebSocketUrlAsync(HttpClient http, int port, CancellationToken ct)
        {
            try
            {
                string raw = await http.GetStringAsync("http://127.0.0.1:" + port + "/json/list");
                List<object> list = Json.Arr(Json.Parse(raw));
                foreach (object item in list)
                {
                    string ws = Json.Str(item, "webSocketDebuggerUrl");
                    string pageUrl = Json.Str(item, "url");
                    if (!string.IsNullOrEmpty(ws) && (string.IsNullOrEmpty(pageUrl) || pageUrl.IndexOf("roblox.com", StringComparison.OrdinalIgnoreCase) >= 0))
                        return ws;
                }
                foreach (object item in list)
                {
                    string ws = Json.Str(item, "webSocketDebuggerUrl");
                    if (!string.IsNullOrEmpty(ws)) return ws;
                }
            }
            catch (Exception) { }
            return null;
        }

        static async Task<string> ReadRoblosecurityViaCdpAsync(string wsUrl, CancellationToken ct)
        {
            try
            {
                Uri uri = new Uri(wsUrl);
                if (!uri.IsLoopback) return null;
                using (ClientWebSocket ws = new ClientWebSocket())
                {
                    await ws.ConnectAsync(uri, ct);
                    byte[] payload = Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Network.getAllCookies\"}");
                    await ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, ct);

                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buf = new byte[8192];
                        while (true)
                        {
                            WebSocketReceiveResult res = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                            if (res.MessageType == WebSocketMessageType.Close) break;
                            ms.Write(buf, 0, res.Count);
                            if (res.EndOfMessage) break;
                        }

                        string json = Encoding.UTF8.GetString(ms.ToArray());
                        object root = Json.Parse(json);
                        object result = Json.Get(root, "result");
                        foreach (object ck in Json.Arr(Json.Get(result, "cookies")))
                        {
                            string name = Json.Str(ck, "name");
                            string domain = Json.Str(ck, "domain");
                            if (name == ".ROBLOSECURITY" && domain.IndexOf("roblox.com", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                string val = Json.Str(ck, "value");
                                if (!string.IsNullOrEmpty(val)) return val;
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        static string SanitizeTargetUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "https://www.roblox.com/login";
            Uri u;
            if (Uri.TryCreate(raw.Trim(), UriKind.Absolute, out u) &&
                u.Scheme == Uri.UriSchemeHttps &&
                (u.Host.Equals("www.roblox.com", StringComparison.OrdinalIgnoreCase) ||
                 u.Host.Equals("accounts.roblox.com", StringComparison.OrdinalIgnoreCase) ||
                 u.Host.Equals("roblox.com", StringComparison.OrdinalIgnoreCase)))
            {
                return u.AbsoluteUri;
            }
            return "https://www.roblox.com/login";
        }

        static int AllocateLoopbackPort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        static string FindBrowser(string pref, out string browserName)
        {
            string pf = Environment.GetEnvironmentVariable("ProgramFiles");
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            string lad = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            string p = (pref ?? "auto").Trim().ToLowerInvariant();

            List<KeyValuePair<string, string>> entries = new List<KeyValuePair<string, string>>();
            Action addEdge = delegate
            {
                if (pf86 != null) entries.Add(new KeyValuePair<string, string>("Microsoft Edge", Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe")));
                if (pf != null) entries.Add(new KeyValuePair<string, string>("Microsoft Edge", Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe")));
                if (lad != null) entries.Add(new KeyValuePair<string, string>("Microsoft Edge", Path.Combine(lad, @"Microsoft\Edge\Application\msedge.exe")));
            };
            Action addChrome = delegate
            {
                if (pf != null) entries.Add(new KeyValuePair<string, string>("Google Chrome", Path.Combine(pf, @"Google\Chrome\Application\chrome.exe")));
                if (pf86 != null) entries.Add(new KeyValuePair<string, string>("Google Chrome", Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe")));
                if (lad != null) entries.Add(new KeyValuePair<string, string>("Google Chrome", Path.Combine(lad, @"Google\Chrome\Application\chrome.exe")));
            };
            Action addBrave = delegate
            {
                if (pf != null) entries.Add(new KeyValuePair<string, string>("Brave", Path.Combine(pf, @"BraveSoftware\Brave-Browser\Application\brave.exe")));
                if (lad != null) entries.Add(new KeyValuePair<string, string>("Brave", Path.Combine(lad, @"BraveSoftware\Brave-Browser\Application\brave.exe")));
            };

            if (p == "chrome") { addChrome(); addEdge(); addBrave(); }
            else if (p == "brave") { addBrave(); addEdge(); addChrome(); }
            else { addEdge(); addChrome(); addBrave(); }

            foreach (KeyValuePair<string, string> kv in entries)
            {
                if (File.Exists(kv.Value))
                {
                    browserName = kv.Key;
                    return kv.Value;
                }
            }
            browserName = "";
            return null;
        }

        static void CleanupDirAsync(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            Task.Run(delegate
            {
                for (int i = 0; i < 6; i++)
                {
                    try
                    {
                        if (!Directory.Exists(dir)) return;
                        Directory.Delete(dir, true);
                        return;
                    }
                    catch (Exception) { Thread.Sleep(400); }
                }
            });
        }
    }
}
