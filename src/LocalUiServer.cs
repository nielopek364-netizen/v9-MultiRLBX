using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MultiRLBX
{
    /// <summary>
    /// Embedded HTTP/JSON loopback server hosting the desktop UI strictly on <c>127.0.0.1</c>
    /// with an ephemeral per-session token (no LAN/external exposure and no Windows Firewall prompts).
    /// </summary>
    public static class LocalUiServer
    {
        static TcpListener localListener;
        static volatile bool running;
        static int localPort;
        static string sessionToken = "";
        static string cachedHtml;

        /// <summary>Loopback URL including the ephemeral session token query string.</summary>
        public static string LocalUrl
        {
            get { return "http://127.0.0.1:" + localPort + "/?t=" + sessionToken; }
        }

        /// <summary>Starts the loopback-only HTTP UI listener on 127.0.0.1.</summary>
        public static void Start()
        {
            Stop();
            sessionToken = NewToken();
            try
            {
                localListener = new TcpListener(IPAddress.Loopback, 19890);
                localListener.Start();
            }
            catch (Exception)
            {
                localListener = new TcpListener(IPAddress.Loopback, 0);
                localListener.Start();
            }
            localPort = ((IPEndPoint)localListener.LocalEndpoint).Port;
            running = true;

            Thread tLocal = new Thread(delegate() { AcceptLoop(localListener); });
            tLocal.IsBackground = true;
            tLocal.Start();

            AppEngine.AddLog("Panel Web UI uruchomiony lokalnie (loopback): " + LocalUrl);
        }

        /// <summary>Stops the loopback TCP listener cleanly.</summary>
        public static void Stop()
        {
            running = false;
            try { if (localListener != null) localListener.Stop(); } catch (Exception) { }
            localListener = null;
        }

        static string NewToken()
        {
            byte[] b = new byte[18];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider()) { rng.GetBytes(b); }
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        static void AcceptLoop(TcpListener listener)
        {
            while (running && listener != null)
            {
                try
                {
                    TcpClient c = listener.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(delegate { HandleClient(c); });
                }
                catch (SocketException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception) { }
            }
        }

        static void HandleClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 10000;
                    NetworkStream ns = client.GetStream();
                    string head;
                    string body;
                    if (!ReadHttpRequest(ns, out head, out body)) return;

                    string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                    if (lines.Length == 0) return;
                    string[] reqParts = lines[0].Split(' ');
                    if (reqParts.Length < 2) return;

                    string method = reqParts[0].ToUpperInvariant();
                    string rawTarget = reqParts[1];
                    int qIdx = rawTarget.IndexOf('?');
                    string path = qIdx >= 0 ? rawTarget.Substring(0, qIdx) : rawTarget;
                    string query = qIdx >= 0 ? rawTarget.Substring(qIdx + 1) : "";

                    string authHeader = "";
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int colon = lines[i].IndexOf(':');
                        if (colon <= 0) continue;
                        string k = lines[i].Substring(0, colon).Trim();
                        string v = lines[i].Substring(colon + 1).Trim();
                        if (k.Equals("X-MultiRLBX-Token", StringComparison.OrdinalIgnoreCase)) authHeader = v;
                    }

                    if (path == "/favicon.ico")
                    {
                        RespondBytes(ns, 204, "image/x-icon", new byte[0]);
                        return;
                    }

                    if (path == "/" || path == "/index.html")
                    {
                        Respond(ns, 200, "text/html; charset=utf-8", LoadUiHtml(true));
                        return;
                    }

                    if (!IsAuthorized(query, authHeader))
                    {
                        RespondJson(ns, 401, new Dictionary<string, object> { { "ok", false }, { "error", "Unauthorized" } });
                        return;
                    }

                    AppEngine.TouchActivity();

                    Dictionary<string, object> result = DispatchApi(method, path, query, body);
                    RespondJson(ns, 200, result);
                }
                catch (Exception ex)
                {
                    Log.Error("LocalUiServer.Handle", ex);
                }
            }
        }

        static bool IsAuthorized(string query, string headerToken)
        {
            if (string.IsNullOrEmpty(sessionToken)) return false;
            string qTok = QueryParam(query, "t");
            return headerToken == sessionToken || qTok == sessionToken;
        }

        static string QueryParam(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return "";
            foreach (string part in query.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string k = Uri.UnescapeDataString(part.Substring(0, eq));
                if (k == key) return Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            return "";
        }

        static string LoadUiHtml(bool injectLocalToken)
        {
            string html = null;
            try
            {
                string disk = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui", "app.html");
                if (File.Exists(disk)) html = File.ReadAllText(disk, Encoding.UTF8);
            }
            catch (Exception) { }

            if (html == null)
            {
                if (cachedHtml == null)
                {
                    try
                    {
                        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("MultiRLBX.app.html"))
                        {
                            if (s != null)
                            {
                                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                                {
                                    cachedHtml = sr.ReadToEnd();
                                }
                            }
                        }
                    }
                    catch (Exception) { }
                }
                html = cachedHtml;
            }

            if (string.IsNullOrEmpty(html))
                return "<!doctype html><html><body style='background:#0b0f17;color:#e5e7eb;font-family:sans-serif;padding:40px'><h2>v9 MultiRLBX v1.0.0 · by v9pv</h2><p>UI asset not found.</p></body></html>";

            if (injectLocalToken && !string.IsNullOrEmpty(sessionToken))
            {
                string tag = "<script>window.__LOCAL_TOKEN__=\"" + sessionToken + "\";</script>";
                int idx = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) html = html.Substring(0, idx) + tag + html.Substring(idx);
                else html = tag + html;
            }
            return html;
        }

        static Dictionary<string, object> DispatchApi(string method, string path, string query, string body)
        {
            object parsed = string.IsNullOrWhiteSpace(body) ? null : Json.Parse(body);

            if (path == "/api/state" && method == "GET") return BuildStatePayload();

            if (path == "/api/unlock" && method == "POST")
            {
                string pw = Json.Str(parsed, "password");
                if (Store.Password == null || pw == Store.Password)
                {
                    AppEngine.IsLocked = false;
                    return OkMsg("Odblokowano / Unlocked");
                }
                return Err("Bledne haslo / Wrong password");
            }

            if (AppEngine.IsLocked) return Err("Locked");

            if (path == "/api/lock" && method == "POST")
            {
                if (Store.Password != null) AppEngine.IsLocked = true;
                return OkMsg("Zablokowano");
            }

            if (path == "/api/accounts/add-cookie" && method == "POST")
            {
                string raw = Json.Str(parsed, "raw");
                string grp = Json.Str(parsed, "group");
                string alias = Json.Str(parsed, "alias");
                List<string> cookies = Util.ExtractCookies(raw);
                if (cookies.Count == 0) return Err("Nie znaleziono poprawnego tokena .ROBLOSECURITY");
                int added, updated;
                string lastErr;
                ImportCookieList(cookies, grp, alias, out added, out updated, out lastErr);
                if (added == 0 && updated == 0)
                    return Err("Nie udalo sie zweryfikowac cookie w Roblox" + (string.IsNullOrEmpty(lastErr) ? "." : ": " + lastErr));
                Store.SaveAccounts();
                string msg = "Dodano konta: +" + added + ", zaktualizowano: " + updated;
                AppEngine.AddLog(msg);
                return new Dictionary<string, object> { { "ok", true }, { "added", added }, { "updated", updated }, { "message", msg } };
            }

            if (path == "/api/accounts/import-json" && method == "POST")
            {
                string raw = Json.Str(parsed, "raw");
                string grp = Json.Str(parsed, "group");
                string alias = Json.Str(parsed, "alias");
                List<string> cookies = ExportImport.ParseJsonCookies(raw);
                if (cookies.Count == 0) return Err("Nie znaleziono tokena .ROBLOSECURITY w danych JSON");
                int added, updated;
                string lastErr;
                ImportCookieList(cookies, grp, alias, out added, out updated, out lastErr);
                if (added == 0 && updated == 0)
                    return Err("Import JSON nie powiodl sie" + (string.IsNullOrEmpty(lastErr) ? "." : ": " + lastErr));
                Store.SaveAccounts();
                string msg = "Import JSON: +" + added + ", zaktualizowano: " + updated;
                AppEngine.AddLog(msg);
                return new Dictionary<string, object> { { "ok", true }, { "added", added }, { "updated", updated }, { "message", msg } };
            }

            if (path == "/api/accounts/bulk-import" && method == "POST")
            {
                string raw = Json.Str(parsed, "raw");
                string grp = Json.Str(parsed, "group");
                string alias = Json.Str(parsed, "alias");
                List<ExportImport.BulkRow> rows = ExportImport.ParseBulk(raw);
                if (rows.Count == 0) return Err("Brak poprawnych linii nick:haslo:cookie");
                int added = 0;
                int updated = 0;
                string lastErr = "";
                foreach (ExportImport.BulkRow r in rows)
                {
                    UserInfo u = Api.Authenticated(r.Cookie).GetAwaiter().GetResult();
                    if (!u.Valid) { if (!string.IsNullOrEmpty(u.Error)) lastErr = u.Error; continue; }
                    Account existing = Store.Accounts.FirstOrDefault(x => x.UserId == u.Id || x.Username.Equals(u.Name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        existing.Cookie = r.Cookie;
                        if (!string.IsNullOrEmpty(r.Pass)) existing.Password = r.Pass;
                        if (!string.IsNullOrEmpty(grp)) existing.Group = grp;
                        if (!string.IsNullOrEmpty(alias)) existing.Alias = alias;
                        existing.LoginLost = false;
                        updated++;
                    }
                    else
                    {
                        Account a = new Account();
                        a.Username = u.Name;
                        a.DisplayName = u.Display;
                        a.UserId = u.Id;
                        a.Cookie = r.Cookie;
                        a.Password = r.Pass;
                        a.Group = grp;
                        a.Alias = alias;
                        a.Order = Store.Accounts.Count;
                        Store.Accounts.Add(a);
                        added++;
                    }
                }
                if (added == 0 && updated == 0)
                    return Err("Import nie powiodl sie: zadne cookie nie przeszlo weryfikacji" + (string.IsNullOrEmpty(lastErr) ? "." : ": " + lastErr));
                if (!string.IsNullOrEmpty(grp) && !Store.Settings.Groups.Contains(grp))
                {
                    Store.Settings.Groups.Add(grp);
                    Store.SaveSettings();
                }
                Store.SaveAccounts();
                string msg = "Import hurtowy: +" + added + ", zaktualizowano: " + updated;
                AppEngine.AddLog(msg);
                return new Dictionary<string, object> { { "ok", true }, { "added", added }, { "updated", updated }, { "message", msg } };
            }

            if (path == "/api/accounts/qr-start" && method == "POST")
            {
                QrSession q = Api.QrCreateAsync().GetAwaiter().GetResult();
                if (!string.IsNullOrEmpty(q.Error)) return Err(q.Error);
                string qrSvg = QrCode.ToSvg(q.ConfirmUrl);
                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "code", q.Code },
                    { "privateKey", q.PrivateKey },
                    { "confirmUrl", q.ConfirmUrl },
                    { "expirationTime", q.ExpirationTime },
                    { "qrSvg", qrSvg }
                };
            }

            if (path == "/api/accounts/qr-poll" && method == "POST")
            {
                string code = Json.Str(parsed, "code");
                string pk = Json.Str(parsed, "privateKey");
                string grp = Json.Str(parsed, "group");
                string alias = Json.Str(parsed, "alias");
                QrPollResult pr = Api.QrPollAsync(code, pk).GetAwaiter().GetResult();
                if (!string.IsNullOrEmpty(pr.Error)) return Err(pr.Error);

                if (!string.IsNullOrEmpty(pr.Cookie))
                {
                    int added, updated;
                    string lastErr;
                    ImportCookieList(new List<string> { pr.Cookie }, grp, alias, out added, out updated, out lastErr);
                    if (added == 0 && updated == 0)
                        return Err("Potwierdzono QR, ale weryfikacja konta nie powiodla sie" + (string.IsNullOrEmpty(lastErr) ? "." : ": " + lastErr));
                    Store.SaveAccounts();
                    string msg = "Zalogowano przez QR (" + pr.AccountName + ")";
                    AppEngine.AddLog(msg);
                    return new Dictionary<string, object>
                    {
                        { "ok", true },
                        { "status", "Validated" },
                        { "completed", true },
                        { "accountName", pr.AccountName },
                        { "message", msg }
                    };
                }

                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "status", string.IsNullOrEmpty(pr.Status) ? "Created" : pr.Status },
                    { "completed", false },
                    { "accountName", pr.AccountName }
                };
            }

            if (path == "/api/accounts/open-confirm" && method == "POST")
            {
                string code = Json.Str(parsed, "code");
                string target = string.IsNullOrWhiteSpace(code)
                    ? "https://www.roblox.com/crossdevicelogin"
                    : "https://www.roblox.com/crossdevicelogin/ConfirmCode?code=" + Uri.EscapeDataString(code.Trim());
                DesktopShell.OpenInDefaultBrowser(target);
                return OkMsg("Otwarto potwierdzenie Quick Login w przegladarce.");
            }

            if (path == "/api/accounts/web-login-start" && method == "POST")
            {
                string url = Json.Str(parsed, "url");
                string grp = Json.Str(parsed, "group");
                string alias = Json.Str(parsed, "alias");
                string browser = Json.Str(parsed, "browser");
                WebLoginStatus st = WebAuth.Start(url, grp, alias, browser);
                if (!string.IsNullOrEmpty(st.Error)) return Err(st.Error);
                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "active", st.Active },
                    { "browser", st.Browser },
                    { "message", "Otwarto izolowane okno logowania (" + st.Browser + ")." }
                };
            }

            if (path == "/api/accounts/web-login-status" && method == "GET")
            {
                WebLoginStatus st = WebAuth.GetStatus();
                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "active", st.Active },
                    { "completed", st.Completed },
                    { "cancelled", st.Cancelled },
                    { "browser", st.Browser },
                    { "username", st.Username },
                    { "error", st.Error }
                };
            }

            if (path == "/api/accounts/web-login-cancel" && method == "POST")
            {
                WebAuth.Cancel();
                return OkMsg("Zatrzymano sesje logowania przez przegladarke.");
            }

            if (path == "/api/accounts/update" && method == "POST")
            {
                string id = Json.Str(parsed, "id");
                Account a = Store.Accounts.FirstOrDefault(x => x.Id == id);
                if (a == null) return Err("Nie znaleziono konta");
                a.Alias = Json.Str(parsed, "alias");
                a.Description = Json.Str(parsed, "description");
                a.Group = Json.Str(parsed, "group");
                a.Tags = Json.Str(parsed, "tags");
                a.SavedPlaceId = Json.Str(parsed, "savedPlaceId");
                a.SavedJobId = Json.Str(parsed, "savedJobId");
                a.GameLink = a.SavedPlaceId;
                a.FpsCap = (int)Json.Long(parsed, "fpsCap");
                a.LaunchDelaySec = (int)Json.Long(parsed, "launchDelaySec");
                a.SeqMinimize = Json.Bool(parsed, "seqMinimize");
                a.SeqTrim = Json.Bool(parsed, "seqTrim");
                string ck = Json.Str(parsed, "cookie");
                if (!string.IsNullOrEmpty(ck)) a.Cookie = ck;
                if (!string.IsNullOrEmpty(a.Group) && !Store.Settings.Groups.Contains(a.Group))
                {
                    Store.Settings.Groups.Add(a.Group);
                    Store.SaveSettings();
                }
                Store.SaveAccounts();
                return OkMsg("Zapisano zmiany konta.");
            }

            if (path == "/api/accounts/delete" && method == "POST")
            {
                string id = Json.Str(parsed, "id");
                Account a = Store.Accounts.FirstOrDefault(x => x.Id == id);
                if (a != null)
                {
                    Launcher.CloseInstancesOf(a, AppEngine.AddLog);
                    Store.Accounts.Remove(a);
                    Store.SaveAccounts();
                }
                return OkMsg("Usunieto konto.");
            }

            if (path == "/api/accounts/check-all" && method == "POST")
            {
                AppEngine.CheckAllCookiesAsync().GetAwaiter().GetResult();
                return OkMsg("Sprawdzono wszystkie cookies.");
            }

            if (path == "/api/accounts/check-presence" && method == "POST")
            {
                int inGame = AppEngine.CheckPresenceAsync().GetAwaiter().GetResult();
                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "inGame", inGame },
                    { "message", "Sprawdzono Presence Roblox (w grze: " + inGame + ")." }
                };
            }

            if (path == "/api/accounts/headshots" && method == "GET")
            {
                string rawNames = QueryParam(query, "usernames");
                string rawIds = QueryParam(query, "userIds");
                List<string> names = new List<string>();
                List<long> ids = new List<long>();
                if (!string.IsNullOrEmpty(rawNames))
                {
                    foreach (string s in rawNames.Split(','))
                    {
                        string clean = s.Trim();
                        if (clean.Length >= 2 && clean.Length <= 32 && names.Count < 50) names.Add(clean);
                    }
                }
                if (!string.IsNullOrEmpty(rawIds))
                {
                    foreach (string s in rawIds.Split(','))
                    {
                        long n;
                        if (long.TryParse(s.Trim(), out n) && n > 0 && ids.Count < 50) ids.Add(n);
                    }
                }
                if (names.Count == 0 && ids.Count == 0)
                {
                    foreach (Account a in Store.Accounts)
                    {
                        if (a.UserId > 0 && ids.Count < 50) ids.Add(a.UserId);
                        else if (!string.IsNullOrEmpty(a.Username) && names.Count < 50) names.Add(a.Username);
                    }
                }
                Dictionary<string, HeadshotEntry> headshots = Api.FetchHeadshotsAsync(names, ids).GetAwaiter().GetResult();
                return new Dictionary<string, object> { { "ok", true }, { "headshots", headshots } };
            }

            if (path == "/api/vault/export" && method == "POST")
            {
                string pw = Json.Str(parsed, "password");
                try
                {
                    string content = Store.ExportVaultString(pw);
                    string file = "v9-MultiRLBX-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".v9vault";
                    AppEngine.AddLog("Wyeksportowano zaszyfrowana kopie .v9vault (" + Store.Accounts.Count + " kont).");
                    return new Dictionary<string, object> { { "ok", true }, { "fileName", file }, { "content", content } };
                }
                catch (Exception ex) { return Err(ex.Message); }
            }

            if (path == "/api/vault/import" && method == "POST")
            {
                string raw = Json.Str(parsed, "content");
                string pw = Json.Str(parsed, "password");
                try
                {
                    int n;
                    Store.ImportVaultString(raw, pw, out n);
                    Hotkeys.SetEnabled(Store.Settings.GlobalHotkeys);
                    AppEngine.AddLog("Zaimportowano kopie .v9vault (konta: " + n + ").");
                    return OkMsg("Przywrocono kopie .v9vault (konta: " + n + ").");
                }
                catch (Exception ex) { return Err("Blad importu .v9vault: " + ex.Message); }
            }

            if (path == "/api/update/check" && method == "POST")
            {
                UpdateInfo ui = Updater.CheckGitHubAsync().GetAwaiter().GetResult();
                return new Dictionary<string, object>
                {
                    { "ok", string.IsNullOrEmpty(ui.Error) },
                    { "current", ui.Current },
                    { "latest", ui.Latest },
                    { "newer", ui.Newer },
                    { "notes", ui.Notes },
                    { "downloadUrl", ui.DownloadUrl },
                    { "error", ui.Error }
                };
            }

            if (path == "/api/launch" && method == "POST")
            {
                bool all = Json.Bool(parsed, "all");
                string grp = Json.Str(parsed, "group");
                List<Account> targets = new List<Account>();
                if (all) targets = Store.Accounts.ToList();
                else if (!string.IsNullOrEmpty(grp)) targets = Store.Accounts.Where(x => (x.Group ?? "") == grp).ToList();
                else
                {
                    foreach (object o in Json.Arr(Json.Get(parsed, "ids")))
                    {
                        string id = Convert.ToString(o);
                        Account a = Store.Accounts.FirstOrDefault(x => x.Id == id);
                        if (a != null) targets.Add(a);
                    }
                }
                if (targets.Count == 0) return Err("Nie wybrano kont do uruchomienia");

                string mode = Json.Str(parsed, "mode");
                string place = Json.Str(parsed, "placeId");
                string job = Json.Str(parsed, "jobId");
                string priv = Json.Str(parsed, "privateLink");
                string fol = Json.Str(parsed, "follow");
                IDictionary<string, object> pd = Json.Obj(parsed);
                if (pd != null)
                {
                    if (pd.ContainsKey("mode") && !string.IsNullOrEmpty(mode)) Store.Settings.LastMode = mode;
                    if (pd.ContainsKey("placeId") && !string.IsNullOrEmpty(place)) Store.Settings.LastPlaceId = place;
                    if (pd.ContainsKey("jobId")) Store.Settings.LastJobId = job ?? "";
                    if (pd.ContainsKey("privateLink")) Store.Settings.LastPrivate = priv ?? "";
                    if (pd.ContainsKey("follow")) Store.Settings.LastFollow = fol ?? "";
                    Store.SaveSettings();
                }

                LaunchSpec specCheck = AppEngine.BuildCurrentSpec();
                bool specValid = specCheck.Kind == "Follow"
                    ? !string.IsNullOrWhiteSpace(specCheck.FollowName)
                    : (specCheck.Kind == "Private"
                        ? !string.IsNullOrWhiteSpace(specCheck.LinkCode)
                        : specCheck.PlaceId > 0);
                bool anySaved = Store.Settings.UseSavedPerAccount && targets.Any(x => Util.ParsePlaceId(!string.IsNullOrEmpty(x.GameLink) ? x.GameLink : x.SavedPlaceId) > 0);
                if (!specValid && !anySaved)
                    return Err("Podaj Place ID gry (np. 155615604) w Uruchamianie lub przypisz Place ID do konta.");

                Task.Run(async delegate { await AppEngine.LaunchAccountsAsync(targets, null); });
                return OkMsg("Kolejka uruchomiona (" + targets.Count + ").");
            }

            if (path == "/api/launch/stop-queue" && method == "POST")
            {
                AppEngine.StopLaunchQueue();
                return OkMsg("Zatrzymano kolejke uruchamiania.");
            }

            if (path == "/api/stop" && method == "POST")
            {
                string account = Json.Str(parsed, "account");
                if (string.IsNullOrEmpty(account)) AppEngine.StopAll();
                else AppEngine.StopAccount(account);
                return OkMsg("Zatrzymano.");
            }

            if (path == "/api/games/search" && method == "GET")
            {
                string q = QueryParam(query, "q");
                string ck = Store.Accounts.Count > 0 ? Store.Accounts[0].Cookie : null;
                List<GameInfo> games = Api.SearchGames(q, ck).GetAwaiter().GetResult();
                return new Dictionary<string, object> { { "ok", true }, { "games", games } };
            }

            if (path == "/api/games/resolve" && method == "GET")
            {
                string input = QueryParam(query, "input");
                long pid = Util.ParsePlaceId(input);
                if (pid <= 0) return Err("Invalid Place ID or Roblox URL");
                string ck = Store.Accounts.Count > 0 ? Store.Accounts[0].Cookie : null;
                GameInfo game = Api.ResolveGameAsync(pid, ck).GetAwaiter().GetResult();
                if (game == null) return Err("Place " + pid + " not found");
                return new Dictionary<string, object> { { "ok", true }, { "game", game } };
            }

            if (path == "/api/servers" && method == "GET")
            {
                long placeId;
                long.TryParse(QueryParam(query, "placeId"), out placeId);
                string sort = QueryParam(query, "sort");
                string cursor = QueryParam(query, "cursor");
                string ck = Store.Accounts.Count > 0 ? Store.Accounts[0].Cookie : null;
                ServerPage sp = Api.Servers(placeId, sort != "Desc", cursor, ck).GetAwaiter().GetResult();
                return new Dictionary<string, object> { { "ok", true }, { "servers", sp.Servers }, { "next", sp.Next }, { "error", sp.Error } };
            }

            if (path == "/api/layout/apply" && method == "POST")
            {
                int moved = AppEngine.ApplyLayout();
                return new Dictionary<string, object> { { "ok", true }, { "moved", moved } };
            }

            if (path == "/api/layout/focus" && method == "POST")
            {
                int pid = (int)Json.Long(parsed, "pid");
                foreach (WinInfo w in WindowLayout.FindRobloxWindows())
                    if (w.Pid == pid && w.HasWindow) WindowLayout.Focus(w.Hwnd);
                return OkMsg("Focused");
            }

            if (path == "/api/layout/minimize" && method == "POST")
            {
                int pid = (int)Json.Long(parsed, "pid");
                foreach (WinInfo w in WindowLayout.FindRobloxWindows())
                    if (w.Pid == pid && w.HasWindow) WindowLayout.Minimize(w.Hwnd);
                return OkMsg("Minimized");
            }

            if (path == "/api/mem/clean" && method == "POST")
            {
                string msg = AppEngine.CleanMemoryAsync().GetAwaiter().GetResult();
                return OkMsg(msg);
            }

            if (path == "/api/presets/save" && method == "POST")
            {
                string name = Json.Str(parsed, "name").Trim();
                if (string.IsNullOrEmpty(name)) return Err("Podaj nazwe presetu");
                Preset p = Store.Settings.Presets.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (p == null) { p = new Preset(); p.Name = name; Store.Settings.Presets.Add(p); }
                p.SavedTicks = DateTime.UtcNow.Ticks;
                p.Settings = PresetSettings.Capture(Store.Settings);
                p.Entries = new List<PresetEntry>();
                List<WinInfo> wins = WindowLayout.FindRobloxWindows();
                int idx = 0;
                foreach (Instance inst in Launcher.SnapshotInstances())
                {
                    if (!Launcher.IsAlive(inst.Pid)) continue;
                    PresetEntry pe = new PresetEntry();
                    pe.AccountId = inst.Account.Id;
                    pe.Username = inst.Account.Username;
                    pe.Spec = inst.Spec != null ? inst.Spec.Copy() : null;
                    pe.GridIndex = idx++;
                    WinInfo wi = wins.FirstOrDefault(x => x.Pid == inst.Pid);
                    if (wi != null && wi.HasWindow)
                    {
                        pe.HasRect = true;
                        pe.X = wi.Rect.X; pe.Y = wi.Rect.Y; pe.W = wi.Rect.Width; pe.H = wi.Rect.Height;
                        pe.Minimized = wi.Minimized;
                    }
                    p.Entries.Add(pe);
                }
                Store.SaveSettings();
                AppEngine.AddLog("Zapisano preset: " + name);
                return OkMsg("Zapisano preset: " + name);
            }

            if (path == "/api/presets/load" && method == "POST")
            {
                string name = Json.Str(parsed, "name");
                Preset p = Store.Settings.Presets.FirstOrDefault(x => x.Name == name);
                if (p == null) return Err("Preset nie istnieje");
                if (p.Settings != null) p.Settings.ApplyTo(Store.Settings);
                Store.SaveSettings();
                List<Account> toLaunch = new List<Account>();
                foreach (PresetEntry e in p.Entries)
                {
                    Account a = Store.Accounts.FirstOrDefault(x => x.Id == e.AccountId);
                    if (a != null) toLaunch.Add(a);
                }
                if (toLaunch.Count > 0)
                    Task.Run(async delegate { await AppEngine.LaunchAccountsAsync(toLaunch, null); });
                return OkMsg("Wczytano preset: " + name);
            }

            if (path == "/api/presets/delete" && method == "POST")
            {
                string name = Json.Str(parsed, "name");
                Store.Settings.Presets.RemoveAll(x => x.Name == name);
                Store.SaveSettings();
                return OkMsg("Usunieto preset.");
            }

            if (path == "/api/webhook/test" && method == "POST")
            {
                string url = Json.Str(parsed, "url");
                bool isEn = Json.Str(parsed, "lang") == "en";
                if (!Webhook.LooksLikeWebhook(url))
                    return Err(isEn ? "Invalid webhook URL (requires https://.../api/webhooks/...)" : "Niepoprawny adres URL webhooka (wymagany https://.../api/webhooks/...)");
                Uri uri;
                string host = Uri.TryCreate(url.Trim(), UriKind.Absolute, out uri) ? uri.Host : "webhook";
                Webhook.SendTo(
                    url,
                    "Test v9 MultiRLBX v1.0.0",
                    isEn ? "Webhook notification channel verified successfully." : "Polaczenie webhook dziala poprawnie.",
                    Webhook.Green);
                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "message", (isEn ? "Test message sent (" : "Wyslano wiadomosc testowa (") + host + ")." },
                    { "validatedHost", host }
                };
            }

            if (path == "/api/macros" && method == "GET")
            {
                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "playing", AppEngine.WebMacroPlayer.Playing },
                    { "activeName", AppEngine.WebMacroPlayer.ActiveName ?? "" },
                    { "recording", MacroRecorder.Recording },
                    { "libraryDir", MacroRecorder.LibraryDir },
                    { "macros", BuildMacroList() }
                };
            }

            if (path == "/api/macros/save" && method == "POST")
            {
                string name = Json.Str(parsed, "name").Trim();
                if (string.IsNullOrEmpty(name)) return Err("Podaj nazwe makra (.v9m)");

                MacroDocument doc = new MacroDocument();
                doc.Name = name;
                doc.Author = Updater.Author;
                doc.Speed = Math.Max(0.1, Math.Min(100.0, Json.Double(parsed, "speed", 1.0)));
                doc.Repeats = Math.Max(1, (int)Json.Long(parsed, "repeats", 1));
                doc.JitterMs = Math.Max(0, (int)Json.Long(parsed, "jitterMs"));
                doc.JitterPx = Math.Max(0, (int)Json.Long(parsed, "jitterPx"));

                foreach (object stepObj in Json.Arr(Json.Get(parsed, "steps")))
                {
                    string kind = Json.Str(stepObj, "kind").ToLowerInvariant();
                    int delay = Math.Max(0, (int)Json.Long(stepObj, "delayMs", 150));
                    if (kind == "wait")
                    {
                        MacroRecorder.AddWait(doc.Events, Math.Max(50, delay));
                    }
                    else if (kind == "click")
                    {
                        int x = (int)Json.Long(stepObj, "x");
                        int y = (int)Json.Long(stepObj, "y");
                        int btn = Math.Max(1, Math.Min(5, (int)Json.Long(stepObj, "button", 1)));
                        MacroRecorder.AddClick(doc.Events, x, y, btn, delay);
                    }
                    else
                    {
                        int vk = (int)Json.Long(stepObj, "vk", 0x20);
                        int hold = Math.Max(20, (int)Json.Long(stepObj, "holdMs", 60));
                        MacroRecorder.AddKey(doc.Events, vk, hold, delay);
                    }
                }

                if (doc.Events.Count == 0) return Err("Dodaj co najmniej jeden krok makra.");
                string file = MacroRecorder.LibraryPathFor(name);
                MacroRecorder.SaveV9m(file, doc);
                AppEngine.AddLog("Zapisano makro .v9m: " + Path.GetFileName(file));
                return OkMsg("Zapisano makro " + Path.GetFileName(file));
            }

            if (path == "/api/macros/play" && method == "POST")
            {
                if (AppEngine.WebMacroPlayer.Playing) return Err("Makro jest juz odtwarzane.");
                string file = Json.Str(parsed, "file");
                string targetPath = MacroRecorder.LibraryPathFor(file);
                if (!File.Exists(targetPath)) return Err("Nie znaleziono pliku .v9m w bibliotece.");

                MacroDocument doc = MacroRecorder.LoadV9m(targetPath);
                if (doc.Events.Count == 0) return Err("Plik .v9m nie zawiera krokow.");

                AppEngine.WebMacroPlayer.Speed = Math.Max(0.1, Json.Double(parsed, "speed", doc.Speed > 0 ? doc.Speed : 1.0));
                AppEngine.WebMacroPlayer.Repeats = Math.Max(1, (int)Json.Long(parsed, "repeats", doc.Repeats > 0 ? doc.Repeats : 1));
                AppEngine.WebMacroPlayer.Continuous = Json.Bool(parsed, "continuous");
                AppEngine.WebMacroPlayer.JitterMs = Math.Max(0, (int)Json.Long(parsed, "jitterMs", doc.JitterMs));
                AppEngine.WebMacroPlayer.JitterPx = Math.Max(0, (int)Json.Long(parsed, "jitterPx", doc.JitterPx));
                AppEngine.WebMacroPlayer.TargetAccountId = Json.Str(parsed, "targetAccountId");
                AppEngine.WebMacroPlayer.ActiveName = doc.Name;
                AppEngine.WebMacroPlayer.Start(doc.Events);
                AppEngine.AddLog("Uruchomiono makro .v9m: " + doc.Name);
                return OkMsg("Odtwarzanie makra .v9m: " + doc.Name);
            }

            if (path == "/api/macros/stop" && method == "POST")
            {
                AppEngine.WebMacroPlayer.Stop();
                AppEngine.AddLog("Zatrzymano odtwarzanie makra .v9m.");
                return OkMsg("Zatrzymano makro.");
            }

            if (path == "/api/macros/delete" && method == "POST")
            {
                string file = Json.Str(parsed, "file");
                string targetPath = MacroRecorder.LibraryPathFor(file);
                if (File.Exists(targetPath)) File.Delete(targetPath);
                return OkMsg("Usunieto plik .v9m.");
            }

            if (path == "/api/macros/open-window" && method == "POST")
            {
                AppEngine.RunOnUi(delegate { MacroForm.ShowOrFocus(); });
                return OkMsg("Otwarto okno Makra (F9 Nagraj / F10 Odtwórz).");
            }

            if (path == "/api/afk/pulse" && method == "POST")
            {
                int poked = AntiAfk.Pulse(AppEngine.AddLog).GetAwaiter().GetResult();
                AppEngine.AddLog("Anti-AFK (test ręczny): odświeżono " + poked + " okien.");
                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "poked", poked },
                    { "message", poked > 0 ? "Impuls Anti-AFK wykonany na " + poked + " oknach." : "Brak działających okien Roblox w tle do odświeżenia." }
                };
            }

            if (path == "/api/macros/record-start" && method == "POST")
            {
                if (AppEngine.WebMacroPlayer.Playing) return Err("Zatrzymaj odtwarzanie makra przed nagrywaniem.");
                bool moves = Json.Bool(parsed, "recordMoves");
                bool hiRes = Json.Bool(parsed, "hiRes");
                bool started = false;
                using (ManualResetEvent done = new ManualResetEvent(false))
                {
                    AppEngine.RunOnUi(delegate
                    {
                        MacroRecorder.RecordMoves = moves;
                        MacroRecorder.HiRes = hiRes;
                        started = MacroRecorder.Start();
                        done.Set();
                    });
                    done.WaitOne(2000);
                }
                if (!started) return Err("System Windows zablokowal rejestracje hooka wejscia.");
                AppEngine.AddLog("Rozpoczeto nagrywanie makra .v9m.");
                return OkMsg("Nagrywanie .v9m aktywne.");
            }

            if (path == "/api/macros/record-stop" && method == "POST")
            {
                string name = Json.Str(parsed, "name").Trim();
                if (string.IsNullOrEmpty(name)) name = "rec_" + DateTime.Now.ToString("HHmmss");
                bool trimIdle = Json.Bool(parsed, "trimIdle");
                List<MacroEvent> captured = null;
                using (ManualResetEvent done = new ManualResetEvent(false))
                {
                    AppEngine.RunOnUi(delegate
                    {
                        MacroRecorder.Stop();
                        captured = new List<MacroEvent>(MacroRecorder.Events);
                        done.Set();
                    });
                    done.WaitOne(2000);
                }
                if (captured == null || captured.Count == 0) return Err("Nie zarejestrowano zadnych krokow.");
                if (trimIdle) MacroRecorder.TrimIdle(captured);
                MacroDocument doc = new MacroDocument();
                doc.Name = name;
                doc.Author = Updater.Author;
                doc.Speed = Math.Max(0.1, Math.Min(100.0, Json.Double(parsed, "speed", 1.0)));
                doc.Repeats = Math.Max(1, (int)Json.Long(parsed, "repeats", 1));
                doc.JitterMs = Math.Max(0, (int)Json.Long(parsed, "jitterMs"));
                doc.JitterPx = Math.Max(0, (int)Json.Long(parsed, "jitterPx"));
                doc.Events = captured;
                string file = MacroRecorder.LibraryPathFor(name);
                MacroRecorder.SaveV9m(file, doc);
                AppEngine.AddLog("Zapisano nagrane makro .v9m: " + Path.GetFileName(file) + " (" + captured.Count + " krokow)");
                return OkMsg("Zapisano nagrane makro " + Path.GetFileName(file) + " (" + captured.Count + " krokow).");
            }

            if (path == "/api/settings" && method == "POST")
            {
                ApplySettingsPayload(parsed);
                return OkMsg("Zapisano ustawienia.");
            }

            if (path == "/api/tools/open-data" && method == "POST")
            {
                try { Process.Start("explorer.exe", Store.Dir); } catch (Exception) { }
                return OkMsg("OK");
            }

            return Err("Unknown endpoint: " + path);
        }

        static void ApplySettingsPayload(object parsed)
        {
            IDictionary<string, object> d = Json.Obj(parsed);
            if (d == null) return;
            AppSettings s = Store.Settings;

            if (d.ContainsKey("language")) { s.Language = Json.Str(d, "language"); Lang.Load(s.Language); }
            if (d.ContainsKey("theme")) { s.Theme = Json.Str(d, "theme"); Theme.Set(s.Theme != "Light"); }
            if (d.ContainsKey("accent")) { s.Accent = Json.Str(d, "accent"); Theme.SetAccent(s.Accent); }
            if (d.ContainsKey("multiInstance"))
            {
                s.MultiInstance = Json.Bool(d, "multiInstance");
                if (s.MultiInstance) MultiInstance.Start(); else MultiInstance.Stop();
            }
            if (d.ContainsKey("launchDelaySec")) s.LaunchDelaySec = Math.Max(0, (int)Json.Long(d, "launchDelaySec"));
            if (d.ContainsKey("fpsCap")) s.FpsCap = Math.Max(0, (int)Json.Long(d, "fpsCap"));
            if (d.ContainsKey("priority")) s.Priority = Json.Str(d, "priority");
            if (d.ContainsKey("autoCloseLast")) s.AutoCloseLast = Json.Bool(d, "autoCloseLast");
            if (d.ContainsKey("useSavedPerAccount")) s.UseSavedPerAccount = Json.Bool(d, "useSavedPerAccount");
            if (d.ContainsKey("autoTileOnBatch")) s.AutoTileOnBatch = Json.Bool(d, "autoTileOnBatch");
            if (d.ContainsKey("autoRelaunch")) s.AutoRelaunch = Json.Bool(d, "autoRelaunch");
            if (d.ContainsKey("watcherSec")) s.WatcherSec = Math.Max(10, (int)Json.Long(d, "watcherSec"));
            if (d.ContainsKey("watcherUsePresence")) s.WatcherUsePresence = Json.Bool(d, "watcherUsePresence");
            if (d.ContainsKey("autoReconnectKick")) s.AutoReconnectKick = Json.Bool(d, "autoReconnectKick");
            if (d.ContainsKey("reconnectIdleSec")) s.ReconnectIdleSec = Math.Max(60, (int)Json.Long(d, "reconnectIdleSec"));
            if (d.ContainsKey("ghostMin")) s.GhostMin = Math.Max(0, (int)Json.Long(d, "ghostMin"));
            if (d.ContainsKey("crashBreaker")) s.CrashBreaker = Json.Bool(d, "crashBreaker");
            if (d.ContainsKey("crashBreakerMax")) s.CrashBreakerMax = Math.Max(1, (int)Json.Long(d, "crashBreakerMax"));
            if (d.ContainsKey("crashBreakerWindowMin")) s.CrashBreakerWindowMin = Math.Max(1, (int)Json.Long(d, "crashBreakerWindowMin"));
            if (d.ContainsKey("crashBreakerCooldownMin")) s.CrashBreakerCooldownMin = Math.Max(1, (int)Json.Long(d, "crashBreakerCooldownMin"));
            if (d.ContainsKey("antiAfk")) s.AntiAfk = Json.Bool(d, "antiAfk");
            if (d.ContainsKey("antiAfkMin")) s.AntiAfkMin = Math.Max(1, (int)Json.Long(d, "antiAfkMin"));
            if (d.ContainsKey("antiAfkAction")) s.AntiAfkAction = Json.Str(d, "antiAfkAction");
            if (d.ContainsKey("scheduleEnabled")) s.ScheduleEnabled = Json.Bool(d, "scheduleEnabled");
            if (d.ContainsKey("scheduleRestartHours")) s.ScheduleRestartHours = Math.Max(0, (int)Json.Long(d, "scheduleRestartHours"));
            if (d.ContainsKey("scheduleStartTime")) s.ScheduleStartTime = Json.Str(d, "scheduleStartTime");
            if (d.ContainsKey("scheduleStopTime")) s.ScheduleStopTime = Json.Str(d, "scheduleStopTime");
            if (d.ContainsKey("scheduleGroup")) s.ScheduleGroup = Json.Str(d, "scheduleGroup");

            if (d.ContainsKey("lastMode")) s.LastMode = Json.Str(d, "lastMode");
            if (d.ContainsKey("lastPlaceId")) s.LastPlaceId = Json.Str(d, "lastPlaceId");
            if (d.ContainsKey("lastJobId")) s.LastJobId = Json.Str(d, "lastJobId");
            if (d.ContainsKey("lastPrivate")) s.LastPrivate = Json.Str(d, "lastPrivate");
            if (d.ContainsKey("lastFollow")) s.LastFollow = Json.Str(d, "lastFollow");

            if (d.ContainsKey("maxAlts"))
            {
                s.MaxAlts = Json.Bool(d, "maxAlts");
                if (!s.MaxAlts) MaxAlts.Reset();
            }
            if (d.ContainsKey("maxAltsFps")) s.MaxAltsFps = Math.Max(0, (int)Json.Long(d, "maxAltsFps"));
            if (d.ContainsKey("maxAltsIdle")) s.MaxAltsIdle = Json.Bool(d, "maxAltsIdle");
            if (d.ContainsKey("maxAltsTrim")) s.MaxAltsTrim = Json.Bool(d, "maxAltsTrim");
            if (d.ContainsKey("maxAltsTrimSmart")) s.MaxAltsTrimSmart = Json.Bool(d, "maxAltsTrimSmart");
            if (d.ContainsKey("maxAltsTrimMinMb")) s.MaxAltsTrimMinMb = Math.Max(0, (int)Json.Long(d, "maxAltsTrimMinMb"));
            if (d.ContainsKey("maxAltsCores")) s.MaxAltsCores = Math.Max(0, (int)Json.Long(d, "maxAltsCores"));
            if (d.ContainsKey("maxAltsMinimize")) s.MaxAltsMinimize = Json.Bool(d, "maxAltsMinimize");

            if (d.ContainsKey("memAutoClean"))
            {
                s.Mem.AutoClean = Json.Bool(d, "memAutoClean");
                if (s.Mem.AutoClean) AppEngine.StartMemHelper();
            }
            if (d.ContainsKey("memThreshold")) s.Mem.Threshold = Math.Max(30, Math.Min(98, (int)Json.Long(d, "memThreshold")));
            if (d.ContainsKey("minFreeRamMb")) s.Mem.MinFreeRamMb = Math.Max(0, (int)Json.Long(d, "minFreeRamMb"));

            if (d.ContainsKey("gfxPreset")) Gfx.ApplyPreset(s.Gfx, Json.Str(d, "gfxPreset"));
            if (d.ContainsKey("gfxApi")) s.Gfx.Api = Json.Str(d, "gfxApi");
            if (d.ContainsKey("writeUnlisted")) s.Gfx.WriteUnlisted = Json.Bool(d, "writeUnlisted");
            if (d.ContainsKey("customClientSettings")) s.CustomClientSettings = Json.Str(d, "customClientSettings");

            if (d.ContainsKey("layoutMode")) s.Layout.Mode = Json.Str(d, "layoutMode");
            if (d.ContainsKey("layoutRows")) s.Layout.Rows = Math.Max(0, (int)Json.Long(d, "layoutRows"));
            if (d.ContainsKey("layoutCols")) s.Layout.Cols = Math.Max(0, (int)Json.Long(d, "layoutCols"));
            if (d.ContainsKey("layoutGap")) s.Layout.Gap = Math.Max(0, (int)Json.Long(d, "layoutGap"));
            if (d.ContainsKey("layoutMargin")) s.Layout.Margin = Math.Max(0, (int)Json.Long(d, "layoutMargin"));
            if (d.ContainsKey("layoutBorderless")) s.Layout.Borderless = Json.Bool(d, "layoutBorderless");
            if (d.ContainsKey("layoutMonitor")) s.Layout.Monitor = Json.Str(d, "layoutMonitor");
            if (d.ContainsKey("layoutLabelTitles")) s.Layout.LabelTitles = Json.Bool(d, "layoutLabelTitles");

            if (d.ContainsKey("webhook")) s.Webhook = Json.Bool(d, "webhook");
            if (d.ContainsKey("webhookUrl")) s.WebhookUrl = Json.Str(d, "webhookUrl");
            if (d.ContainsKey("webhookStatusUrl")) s.WebhookStatusUrl = Json.Str(d, "webhookStatusUrl");
            if (d.ContainsKey("webhookName")) s.WebhookName = Json.Str(d, "webhookName");
            if (d.ContainsKey("webhookCrash")) s.WebhookCrash = Json.Bool(d, "webhookCrash");
            if (d.ContainsKey("webhookRelaunch")) s.WebhookRelaunch = Json.Bool(d, "webhookRelaunch");
            if (d.ContainsKey("webhookBatch")) s.WebhookBatch = Json.Bool(d, "webhookBatch");
            if (d.ContainsKey("webhookLocked")) s.WebhookLocked = Json.Bool(d, "webhookLocked");
            if (d.ContainsKey("webhookMem")) s.WebhookMem = Json.Bool(d, "webhookMem");
            if (d.ContainsKey("dailyReport")) s.DailyReport = Json.Bool(d, "dailyReport");
            if (d.ContainsKey("dailyTime")) s.DailyTime = Json.Str(d, "dailyTime");

            if (d.ContainsKey("lockIdleMin")) s.LockIdleMin = Math.Max(0, (int)Json.Long(d, "lockIdleMin"));
            if (d.ContainsKey("globalHotkeys"))
            {
                s.GlobalHotkeys = Json.Bool(d, "globalHotkeys");
                Hotkeys.SetEnabled(s.GlobalHotkeys);
            }
            if (d.ContainsKey("startupGroup")) s.StartupGroup = Json.Str(d, "startupGroup");

            Store.SaveSettings();
        }

        static Dictionary<string, object> BuildStatePayload()
        {
            int load = 0; long total = 0; long avail = 0;
            MemClean.Status(out load, out total, out avail);

            List<WinInfo> wins = WindowLayout.FindRobloxWindows();
            List<Instance> insts = Launcher.SnapshotInstances();
            List<object> accs = new List<object>();

            foreach (Account a in Store.Accounts)
            {
                Instance runningInst = insts.FirstOrDefault(x => x.Account == a && Launcher.IsAlive(x.Pid));
                LiveInfo li = AppState.Live(a);
                UsageSample samp = null;
                if (runningInst != null) UsageMonitor.Samples.TryGetValue(runningInst.Pid, out samp);

                accs.Add(new Dictionary<string, object>
                {
                    { "id", a.Id },
                    { "username", a.Username },
                    { "displayName", a.DisplayName },
                    { "userId", a.UserId },
                    { "alias", a.Alias },
                    { "name", AppState.Name(a) },
                    { "group", a.Group ?? "" },
                    { "tags", a.Tags ?? "" },
                    { "description", a.Description ?? "" },
                    { "savedPlaceId", a.SavedPlaceId ?? "" },
                    { "savedJobId", a.SavedJobId ?? "" },
                    { "fpsCap", a.FpsCap },
                    { "launchDelaySec", a.LaunchDelaySec },
                    { "seqMinimize", a.SeqMinimize },
                    { "seqTrim", a.SeqTrim },
                    { "loginLost", a.LoginLost },
                    { "locked", li.Locked },
                    { "presenceType", li.PresenceType },
                    { "presenceWhere", li.Where ?? "" },
                    { "running", runningInst != null },
                    { "pid", runningInst != null ? runningInst.Pid : 0 },
                    { "cpu", samp != null ? Math.Round(samp.Cpu, 1) : 0 },
                    { "ramMb", samp != null ? (samp.Ram / 1048576L) : 0 },
                    { "lastUsed", Util.Ago(a.LastUsedTicks) }
                });
            }

            List<object> winList = new List<object>();
            foreach (WinInfo w in wins)
            {
                UsageSample samp = null;
                UsageMonitor.Samples.TryGetValue(w.Pid, out samp);
                winList.Add(new Dictionary<string, object>
                {
                    { "pid", w.Pid },
                    { "account", w.Account },
                    { "title", w.Title },
                    { "x", w.Rect.X },
                    { "y", w.Rect.Y },
                    { "w", w.Rect.Width },
                    { "h", w.Rect.Height },
                    { "minimized", w.Minimized },
                    { "hasWindow", w.HasWindow },
                    { "cpu", samp != null ? Math.Round(samp.Cpu, 1) : 0 },
                    { "ramMb", samp != null ? (samp.Ram / 1048576L) : 0 }
                });
            }

            List<object> monList = new List<object>();
            foreach (MonitorDesc m in WindowLayout.GetMonitors())
            {
                monList.Add(new Dictionary<string, object>
                {
                    { "device", m.Device },
                    { "label", m.Label },
                    { "width", m.Bounds.Width },
                    { "height", m.Bounds.Height },
                    { "primary", m.Primary }
                });
            }

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "version", Updater.CurrentVersion() },
                { "author", Updater.Author },
                { "github", Updater.GithubUrl },
                { "localUrl", LocalUrl },
                { "dataDir", Store.Dir ?? "" },
                { "robloxFound", !string.IsNullOrEmpty(Launcher.FindRobloxExe()) },
                { "locked", AppEngine.IsLocked },
                { "hasPassword", Store.Password != null },
                { "multiInstanceState", MultiInstance.State },
                { "launching", AppEngine.LaunchBusy },
                { "queueTotal", AppEngine.QueueTotal },
                { "queueDone", AppEngine.QueueDone },
                { "queueCurrent", AppEngine.QueueCurrent },
                { "totalLaunches", AppEngine.TotalLaunches },
                { "runningCount", UsageMonitor.Count },
                { "robloxCpu", Math.Round(UsageMonitor.TotalCpu, 1) },
                { "robloxRamMb", UsageMonitor.TotalRam / 1048576L },
                { "memLoad", load },
                { "freeRamMb", avail / 1048576L },
                { "totalRamMb", total / 1048576L },
                { "ramHistory", UsageMonitor.Snapshot(UsageMonitor.SystemFreeMbHist) },
                { "robloxRamHistory", UsageMonitor.Snapshot(UsageMonitor.RobloxRamMbHist) },
                { "robloxCpuHistory", UsageMonitor.Snapshot(UsageMonitor.RobloxCpuHist) },
                { "macroPlaying", AppEngine.WebMacroPlayer.Playing },
                { "macroActiveName", AppEngine.WebMacroPlayer.ActiveName ?? "" },
                { "macroRecording", MacroRecorder.Recording },
                { "macroRecordedSteps", MacroRecorder.Events.Count },
                { "macros", BuildMacroList() },
                { "accounts", accs },
                { "windows", winList },
                { "monitors", monList },
                { "settings", Store.Settings },
                { "gfxPreview", Gfx.Preview() },
                { "logs", AppEngine.GetLogs() }
            };
        }

        static List<object> BuildMacroList()
        {
            List<object> list = new List<object>();
            foreach (MacroDocument d in MacroRecorder.ListLibrary())
            {
                list.Add(new Dictionary<string, object>
                {
                    { "name", d.Name },
                    { "file", Path.GetFileName(d.FilePath) },
                    { "author", d.Author },
                    { "steps", d.Events.Count },
                    { "lengthMs", d.LengthMs },
                    { "speed", d.Speed },
                    { "repeats", d.Repeats },
                    { "jitterMs", d.JitterMs },
                    { "jitterPx", d.JitterPx }
                });
            }
            return list;
        }

        static Dictionary<string, object> OkMsg(string msg)
        {
            return new Dictionary<string, object> { { "ok", true }, { "message", msg } };
        }

        static Dictionary<string, object> Err(string msg)
        {
            return new Dictionary<string, object> { { "ok", false }, { "error", msg } };
        }

        static int FindHeaderEnd(byte[] data, int len)
        {
            for (int i = 0; i <= len - 4; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    return i;
            }
            return -1;
        }

        static bool ReadHttpRequest(NetworkStream s, out string head, out string body)
        {
            head = "";
            body = "";
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] buf = new byte[4096];
                int headerEnd = -1;
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);

                while (headerEnd < 0)
                {
                    if (DateTime.UtcNow > deadline) return false;
                    if (!s.DataAvailable)
                    {
                        Thread.Sleep(5);
                        continue;
                    }
                    int n = s.Read(buf, 0, buf.Length);
                    if (n <= 0) return false;
                    ms.Write(buf, 0, n);
                    byte[] cur = ms.GetBuffer();
                    headerEnd = FindHeaderEnd(cur, (int)ms.Length);
                    if (ms.Length > 65536 && headerEnd < 0) return false;
                }

                byte[] all = ms.GetBuffer();
                int totalLen = (int)ms.Length;
                head = Encoding.UTF8.GetString(all, 0, headerEnd);

                int contentLen = 0;
                foreach (string line in head.Split(new string[] { "\r\n" }, StringSplitOptions.None))
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    if (line.Substring(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        int.TryParse(line.Substring(colon + 1).Trim(), out contentLen);
                        break;
                    }
                }

                if (contentLen <= 0 || contentLen > 4 * 1024 * 1024) return true;

                int bodyStart = headerEnd + 4;
                int haveBody = totalLen - bodyStart;
                byte[] bodyBuf = new byte[contentLen];
                int copied = Math.Min(haveBody, contentLen);
                if (copied > 0) Buffer.BlockCopy(all, bodyStart, bodyBuf, 0, copied);

                DateTime bodyDeadline = DateTime.UtcNow.AddSeconds(5);
                while (copied < contentLen && DateTime.UtcNow < bodyDeadline)
                {
                    if (!s.DataAvailable)
                    {
                        Thread.Sleep(5);
                        continue;
                    }
                    int r = s.Read(bodyBuf, copied, contentLen - copied);
                    if (r <= 0) break;
                    copied += r;
                }
                body = Encoding.UTF8.GetString(bodyBuf, 0, copied);
                return true;
            }
        }

        static void RespondJson(NetworkStream s, int code, object obj)
        {
            Respond(s, code, "application/json; charset=utf-8", Json.To(obj));
        }

        static void Respond(NetworkStream s, int code, string type, string body)
        {
            RespondBytes(s, code, type, Encoding.UTF8.GetBytes(body ?? ""));
        }

        static void RespondBytes(NetworkStream s, int code, string type, byte[] b)
        {
            StringBuilder h = new StringBuilder();
            h.Append("HTTP/1.1 ").Append(code).Append(" ").Append(code == 200 ? "OK" : (code == 204 ? "No Content" : "Error")).Append("\r\n");
            h.Append("Content-Type: ").Append(type).Append("\r\n");
            h.Append("Content-Length: ").Append(b.Length).Append("\r\n");
            h.Append("Cache-Control: no-store\r\nConnection: close\r\n\r\n");
            byte[] head = Encoding.UTF8.GetBytes(h.ToString());
            s.Write(head, 0, head.Length);
            if (b.Length > 0) s.Write(b, 0, b.Length);
        }

        static void ImportCookieList(List<string> cookies, string grp, string alias, out int added, out int updated, out string lastErr)
        {
            added = 0;
            updated = 0;
            lastErr = "";
            for (int i = 0; i < cookies.Count; i++)
            {
                UserInfo u = Api.Authenticated(cookies[i]).GetAwaiter().GetResult();
                if (!u.Valid) { if (!string.IsNullOrEmpty(u.Error)) lastErr = u.Error; continue; }
                Account existing = Store.Accounts.FirstOrDefault(x => x.UserId == u.Id || x.Username.Equals(u.Name, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.Cookie = cookies[i];
                    existing.Username = u.Name;
                    existing.DisplayName = u.Display;
                    existing.LoginLost = false;
                    if (!string.IsNullOrEmpty(grp)) existing.Group = grp;
                    if (!string.IsNullOrEmpty(alias)) existing.Alias = alias;
                    updated++;
                }
                else
                {
                    Account a = new Account();
                    a.Username = u.Name;
                    a.DisplayName = u.Display;
                    a.UserId = u.Id;
                    a.Cookie = cookies[i];
                    a.Group = grp;
                    a.Alias = alias;
                    a.Order = Store.Accounts.Count;
                    Store.Accounts.Add(a);
                    added++;
                }
            }
            if ((added > 0 || updated > 0) && !string.IsNullOrEmpty(grp) && !Store.Settings.Groups.Contains(grp))
            {
                Store.Settings.Groups.Add(grp);
                Store.SaveSettings();
            }
        }
    }
}
