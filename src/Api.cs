using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MultiRLBX
{
    public class ApiResult
    {
        public HttpStatusCode Status;
        public string Body = "";
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool Ok { get { return (int)Status >= 200 && (int)Status < 300; } }
        public string Header(string name)
        {
            string v;
            return Headers.TryGetValue(name, out v) ? v : "";
        }
    }

    public class UserInfo
    {
        public long Id;
        public string Name = "";
        public string Display = "";
        public string Error = "";
        public bool Valid;
        public bool Locked;             // HTTP 403: account is locked / blocked / moderated by Roblox
    }

    public class TicketResult
    {
        public string Ticket;
        public string Error = "";
        public bool Unauthorized;
        public bool Locked;             // HTTP 403: account is locked / blocked / moderated by Roblox
    }

    public class PresenceInfo
    {
        public int Type;
        public string Location = "";
        public long PlaceId;
        public string GameId = "";
    }

    public class ServerInfo
    {
        public string Id = "";
        public int Playing;
        public int Max;
        public int Ping;
        public double Fps;
    }

    public class ServerPage
    {
        public List<ServerInfo> Servers = new List<ServerInfo>();
        public string Next = "";
        public string Error = "";
    }

    public class GameInfo
    {
        public string Name = "";
        public long PlaceId;
        public long UniverseId;
        public string Creator = "";
        public string Genre = "";
        public long Players;
        public long Visits;
        public int MaxPlayers;
        public long UpVotes;
        public long DownVotes;
        public int LikeRatio;
        public string IconUrl = "";
    }

    public class HeadshotEntry
    {
        public long UserId;
        public string Username = "";
        public string DisplayName = "";
        public string HeadshotUrl = "";
    }

    public class QrSession
    {
        public string Code = "";
        public string PrivateKey = "";
        public string ConfirmUrl = "";
        public string ExpirationTime = "";
        public string Status = "";
        public string Error = "";
    }

    public class QrPollResult
    {
        public string Status = "";
        public string AccountName = "";
        public string Cookie = "";
        public string Error = "";
    }

    /// <summary>
    /// Asynchronous Roblox Web API client handling CSRF token handshakes, authentication tickets,
    /// batch cookie verification, server browser queries, presence lookups, and share-link resolution.
    /// </summary>
    public static class Api
    {
        static readonly HttpClient Http;

        static Api()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            HttpClientHandler h = new HttpClientHandler();
            h.UseCookies = false;
            h.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            Http = new HttpClient(h);
            Http.Timeout = TimeSpan.FromSeconds(25);
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        }

        static async Task<ApiResult> Req(HttpMethod method, string url, string cookie, string jsonBody, string csrf, Dictionary<string, string> extra)
        {
            using (HttpRequestMessage req = new HttpRequestMessage(method, url))
            {
                if (!string.IsNullOrEmpty(cookie)) req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
                if (csrf != null) req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
                if (extra == null || !extra.ContainsKey("Referer")) req.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
                if (extra == null || !extra.ContainsKey("Origin")) req.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");
                if (extra != null)
                    foreach (KeyValuePair<string, string> kv in extra) if (!string.IsNullOrEmpty(kv.Value)) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                if (jsonBody != null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                using (HttpResponseMessage resp = await Http.SendAsync(req))
                {
                    ApiResult r = new ApiResult();
                    r.Status = resp.StatusCode;
                    r.Body = await resp.Content.ReadAsStringAsync();
                    foreach (KeyValuePair<string, IEnumerable<string>> h in resp.Headers)
                    {
                        string sep = h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ? "\n" : ",";
                        r.Headers[h.Key] = string.Join(sep, h.Value.ToArray());
                    }
                    return r;
                }
            }
        }

        // POST that transparently handles Roblox's CSRF handshake (first try -> 403 + token, second try works).
        static async Task<ApiResult> PostCsrf(string url, string cookie, string body, Dictionary<string, string> extra)
        {
            ApiResult r = await Req(HttpMethod.Post, url, cookie, body, null, extra);
            if ((int)r.Status == 403)
            {
                string token = r.Header("x-csrf-token");
                if (!string.IsNullOrEmpty(token)) r = await Req(HttpMethod.Post, url, cookie, body, token, extra);
            }
            return r;
        }

        public static async Task<bool> InternetOk()
        {
            try
            {
                ApiResult r = await Req(HttpMethod.Get, "https://users.roblox.com/v1/users/1", null, null, null, null);
                return (int)r.Status > 0;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Validates a <c>.ROBLOSECURITY</c> cookie against <c>users.roblox.com/v1/users/authenticated</c>.</summary>
        public static async Task<UserInfo> Authenticated(string cookie)
        {
            UserInfo u = new UserInfo();
            try
            {
                ApiResult r = await Req(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", cookie, null, null, null);
                if (r.Status == HttpStatusCode.Unauthorized) { u.Error = "Cookie expired or invalid"; return u; }
                if ((int)r.Status == 403) { u.Locked = true; u.Error = "Account locked or blocked by Roblox (HTTP 403)"; return u; }
                if ((int)r.Status == 429) { u.Error = "Rate limited (HTTP 429) - try again in a minute"; return u; }
                if (!r.Ok) { u.Error = "HTTP " + (int)r.Status; return u; }
                object j = Json.Parse(r.Body);
                u.Id = Json.Long(j, "id");
                u.Name = Json.Str(j, "name");
                u.Display = Json.Str(j, "displayName");
                u.Valid = u.Id > 0;
                if (!u.Valid) u.Error = "Unexpected response";
            }
            catch (Exception ex) { u.Error = ex.Message; }
            return u;
        }

        /// <summary>Validates multiple accounts in parallel with bounded concurrency via <see cref="System.Threading.SemaphoreSlim"/>.</summary>
        public static async Task<Dictionary<string, UserInfo>> CheckCookiesBatch(List<Account> accounts, int concurrency)
        {
            Dictionary<string, UserInfo> results = new Dictionary<string, UserInfo>();
            if (accounts == null || accounts.Count == 0) return results;
            System.Threading.SemaphoreSlim gate = new System.Threading.SemaphoreSlim(Math.Max(1, Math.Min(8, concurrency)));
            object mapLock = new object();
            List<Task> tasks = new List<Task>();
            for (int i = 0; i < accounts.Count; i++)
            {
                Account acc = accounts[i];
                if (acc == null || string.IsNullOrEmpty(acc.Cookie)) continue;
                tasks.Add(Task.Run(async delegate
                {
                    await gate.WaitAsync();
                    try
                    {
                        UserInfo info = await Authenticated(acc.Cookie);
                        lock (mapLock) { results[acc.Id] = info; }
                    }
                    finally { gate.Release(); }
                }));
            }
            await Task.WhenAll(tasks);
            return results;
        }

        /// <summary>Requests a single-use <c>rbx-authentication-ticket</c> for launching a Roblox client session.</summary>
        public static async Task<TicketResult> GetTicket(string cookie)
        {
            TicketResult t = new TicketResult();
            try
            {
                Dictionary<string, string> hdr = new Dictionary<string, string>();
                hdr["Referer"] = "https://www.roblox.com/games/4924922222/Brookhaven-RP";
                hdr["Origin"] = "";                         // like Roblox Account Manager: no Origin header on the ticket request
                ApiResult r = await PostCsrf("https://auth.roblox.com/v1/authentication-ticket/", cookie, "", hdr);
                if (r.Status == HttpStatusCode.Unauthorized) { t.Unauthorized = true; t.Error = "cookie expired or invalid (HTTP 401)"; return t; }
                if ((int)r.Status == 403) { t.Locked = true; t.Error = "account locked or blocked by Roblox (HTTP 403)"; return t; }
                if ((int)r.Status == 429) { t.Error = "rate limited by Roblox (HTTP 429) - wait a bit and retry"; return t; }
                string ticket = r.Header("rbx-authentication-ticket");
                if (!r.Ok || string.IsNullOrEmpty(ticket)) { t.Error = "no authentication ticket received (HTTP " + (int)r.Status + ")"; return t; }
                t.Ticket = ticket;
            }
            catch (Exception ex) { t.Error = ex.Message; }
            return t;
        }

        // Exchanges the cookie for a brand-new one (this signs the account out of its other sessions).
        public static async Task<string> RotateCookie(string cookie, Action<string> log)
        {
            try
            {
                TicketResult t = await GetTicket(cookie);
                if (t.Ticket == null) { log("cookie refresh failed: " + t.Error); return null; }
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["authenticationTicket"] = t.Ticket;
                Dictionary<string, string> h = new Dictionary<string, string>();
                h["RBXAuthenticationNegotiation"] = "1";
                ApiResult r = await PostCsrf("https://auth.roblox.com/v1/authentication-ticket/redeem", null, Json.To(body), h);
                Match m = Regex.Match(r.Header("Set-Cookie"), @"\.ROBLOSECURITY=([^;\s]+)");
                if (!m.Success) { log("cookie refresh failed: Roblox did not return a new cookie (HTTP " + (int)r.Status + ")"); return null; }
                string nc = m.Groups[1].Value;
                UserInfo u = await Authenticated(nc);
                if (!u.Valid) { log("cookie refresh failed: the new cookie did not validate (" + u.Error + ")"); return null; }
                return nc;
            }
            catch (Exception ex) { log("cookie refresh error: " + ex.Message); return null; }
        }

        /// <summary>Creates a Roblox cross-device Quick Login challenge (QR + 6-character code).</summary>
        public static async Task<QrSession> QrCreateAsync()
        {
            QrSession q = new QrSession();
            try
            {
                ApiResult r = await PostCsrf("https://apis.roblox.com/auth-token-service/v1/login/create", null, "{}", null);
                if ((int)r.Status == 429) { q.Error = "Limit zapytan Roblox (HTTP 429) - sprobuj za chwile."; return q; }
                if (!r.Ok) { q.Error = "Blad API Quick Login (HTTP " + (int)r.Status + ")"; return q; }
                object j = Json.Parse(r.Body);
                q.Code = Json.Str(j, "code");
                q.PrivateKey = Json.Str(j, "privateKey");
                q.ExpirationTime = Json.Str(j, "expirationTime");
                q.Status = Json.Str(j, "status");
                if (string.IsNullOrEmpty(q.Code) || string.IsNullOrEmpty(q.PrivateKey))
                {
                    q.Error = "Nie otrzymano kodu Quick Login z serwerow Roblox.";
                    return q;
                }
                q.ConfirmUrl = "https://www.roblox.com/crossdevicelogin/ConfirmCode?code=" + Uri.EscapeDataString(q.Code);
            }
            catch (Exception ex) { q.Error = ex.Message; }
            return q;
        }

        /// <summary>Polls a Quick Login session and redeems <c>.ROBLOSECURITY</c> once validated.</summary>
        public static async Task<QrPollResult> QrPollAsync(string code, string privateKey)
        {
            QrPollResult res = new QrPollResult();
            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(privateKey))
            {
                res.Error = "Brak kodu lub klucza sesji QR.";
                return res;
            }
            try
            {
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["code"] = code;
                body["privateKey"] = privateKey;
                ApiResult r = await PostCsrf("https://apis.roblox.com/auth-token-service/v1/login/status", null, Json.To(body), null);
                if (!r.Ok)
                {
                    res.Error = "HTTP " + (int)r.Status;
                    return res;
                }
                object j = Json.Parse(r.Body);
                res.Status = Json.Str(j, "status");
                res.AccountName = Json.Str(j, "accountName");

                if (res.Status.Equals("Validated", StringComparison.OrdinalIgnoreCase))
                {
                    Dictionary<string, object> loginBody = new Dictionary<string, object>();
                    loginBody["ctype"] = "AuthToken";
                    loginBody["cvalue"] = code;
                    loginBody["password"] = privateKey;
                    ApiResult lr = await PostCsrf("https://auth.roblox.com/v2/login", null, Json.To(loginBody), null);
                    Match m = Regex.Match(lr.Header("Set-Cookie"), @"\.ROBLOSECURITY=([^;\s]+)");
                    if (m.Success)
                    {
                        res.Cookie = m.Groups[1].Value;
                    }
                    else
                    {
                        res.Error = "Nie udalo sie odebrac cookie po potwierdzeniu QR (HTTP " + (int)lr.Status + ")";
                    }
                }
            }
            catch (Exception ex) { res.Error = ex.Message; }
            return res;
        }

        public static async Task<Dictionary<long, PresenceInfo>> Presence(string cookie, List<long> ids)
        {
            Dictionary<long, PresenceInfo> res = new Dictionary<long, PresenceInfo>();
            if (ids.Count == 0) return res;
            try
            {
                for (int i = 0; i < ids.Count; i += 100)
                {
                    List<long> chunk = ids.Skip(i).Take(100).ToList();
                    Dictionary<string, object> body = new Dictionary<string, object>();
                    body["userIds"] = chunk.ToArray();
                    ApiResult r = await PostCsrf("https://presence.roblox.com/v1/presence/users", cookie, Json.To(body), null);
                    if (!r.Ok) continue;
                    object j = Json.Parse(r.Body);
                    foreach (object p in Json.Arr(Json.Get(j, "userPresences")))
                    {
                        PresenceInfo pi = new PresenceInfo();
                        pi.Type = (int)Json.Long(p, "userPresenceType");
                        pi.Location = Json.Str(p, "lastLocation");
                        pi.PlaceId = Json.Long(p, "placeId");
                        pi.GameId = Json.Str(p, "gameId");
                        res[Json.Long(p, "userId")] = pi;
                    }
                }
            }
            catch (Exception ex) { Log.Write("Presence lookup failed: " + ex.Message); }
            return res;
        }

        public static async Task<UserInfo> UserByName(string name)
        {
            UserInfo u = new UserInfo();
            try
            {
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["usernames"] = new string[] { name };
                body["excludeBannedUsers"] = false;
                ApiResult r = await PostCsrf("https://users.roblox.com/v1/usernames/users", null, Json.To(body), null);
                if (!r.Ok) { u.Error = "HTTP " + (int)r.Status; return u; }
                foreach (object d in Json.Arr(Json.Get(Json.Parse(r.Body), "data")))
                {
                    u.Id = Json.Long(d, "id");
                    u.Name = Json.Str(d, "name");
                    u.Valid = u.Id > 0;
                    return u;
                }
                u.Error = "User not found";
            }
            catch (Exception ex) { u.Error = ex.Message; }
            return u;
        }

        public static async Task<ServerPage> Servers(long placeId, bool ascending, string cursor, string cookie)
        {
            ServerPage sp = new ServerPage();
            try
            {
                string url = "https://games.roblox.com/v1/games/" + placeId + "/servers/Public?sortOrder=" + (ascending ? "Asc" : "Desc") + "&excludeFullGames=false&limit=100";
                if (!string.IsNullOrEmpty(cursor)) url += "&cursor=" + Uri.EscapeDataString(cursor);
                ApiResult r = await Req(HttpMethod.Get, url, cookie, null, null, null);
                if ((int)r.Status == 429) { sp.Error = "Rate limited by Roblox (HTTP 429). Wait a minute and try again."; return sp; }
                if (!r.Ok) { sp.Error = "HTTP " + (int)r.Status + " " + Util.Trunc(r.Body, 100); return sp; }
                object j = Json.Parse(r.Body);
                sp.Next = Json.Str(j, "nextPageCursor");
                foreach (object s in Json.Arr(Json.Get(j, "data")))
                {
                    ServerInfo si = new ServerInfo();
                    si.Id = Json.Str(s, "id");
                    si.Playing = (int)Json.Long(s, "playing");
                    si.Max = (int)Json.Long(s, "maxPlayers");
                    si.Ping = (int)Json.Long(s, "ping");
                    object f = Json.Get(s, "fps");
                    if (f != null) { try { si.Fps = Convert.ToDouble(f, CultureInfo.InvariantCulture); } catch (Exception) { } }
                    if (si.Id.Length > 0) sp.Servers.Add(si);
                }
            }
            catch (Exception ex) { sp.Error = ex.Message; }
            return sp;
        }

        public static async Task<byte[]> Download(string url)
        {
            try
            {
                using (HttpResponseMessage resp = await Http.GetAsync(url))
                {
                    if (!resp.IsSuccessStatusCode) return null;
                    return await resp.Content.ReadAsByteArrayAsync();
                }
            }
            catch (Exception) { return null; }
        }

        public static async Task<string> PlaceName(long placeId)
        {
            try
            {
                ApiResult r = await Req(HttpMethod.Get, "https://apis.roblox.com/universes/v1/places/" + placeId + "/universe", null, null, null, null);
                if (!r.Ok) return "";
                long uni = Json.Long(Json.Parse(r.Body), "universeId");
                if (uni == 0) return "";
                ApiResult g = await Req(HttpMethod.Get, "https://games.roblox.com/v1/games?universeIds=" + uni, null, null, null, null);
                if (!g.Ok) return "";
                foreach (object d in Json.Arr(Json.Get(Json.Parse(g.Body), "data")))
                    return Json.Str(d, "name");
            }
            catch (Exception) { }
            return "";
        }

        sealed class DirGameCandidate
        {
            public long PlaceId;
            public long UniverseId;
            public string Name = "";
            public long Players;
            public string IconHint = "";
        }

        sealed class CachedHeadshot
        {
            public HeadshotEntry Entry;
            public long ExpiresTicks;
        }

        sealed class CachedGame
        {
            public GameInfo Info;
            public long ExpiresTicks;
        }

        static readonly object CacheLock = new object();
        static readonly Dictionary<string, CachedHeadshot> HeadshotCache = new Dictionary<string, CachedHeadshot>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<long, CachedGame> GameCache = new Dictionary<long, CachedGame>();
        static List<DirGameCandidate> CachedDirectory;
        static long DirectoryExpiresTicks;

        public static async Task<Dictionary<string, HeadshotEntry>> FetchHeadshotsAsync(List<string> usernames, List<long> userIds)
        {
            Dictionary<string, HeadshotEntry> res = new Dictionary<string, HeadshotEntry>(StringComparer.OrdinalIgnoreCase);
            List<string> missingNames = new List<string>();
            HashSet<long> targetIds = new HashSet<long>();
            long nowTicks = DateTime.UtcNow.Ticks;

            lock (CacheLock)
            {
                if (userIds != null)
                {
                    foreach (long id in userIds)
                    {
                        if (id <= 0) continue;
                        CachedHeadshot ch;
                        string idKey = "id:" + id.ToString(CultureInfo.InvariantCulture);
                        if (HeadshotCache.TryGetValue(idKey, out ch) && ch.ExpiresTicks > nowTicks)
                        {
                            res[id.ToString(CultureInfo.InvariantCulture)] = ch.Entry;
                            if (!string.IsNullOrEmpty(ch.Entry.Username))
                                res[ch.Entry.Username.ToLowerInvariant()] = ch.Entry;
                        }
                        else
                        {
                            targetIds.Add(id);
                        }
                    }
                }

                if (usernames != null)
                {
                    foreach (string raw in usernames)
                    {
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        string clean = raw.Trim();
                        string key = clean.ToLowerInvariant();
                        CachedHeadshot ch;
                        if (HeadshotCache.TryGetValue("name:" + key, out ch) && ch.ExpiresTicks > nowTicks)
                        {
                            res[key] = ch.Entry;
                            if (ch.Entry.UserId > 0)
                                res[ch.Entry.UserId.ToString(CultureInfo.InvariantCulture)] = ch.Entry;
                        }
                        else if (!missingNames.Any(x => x.Equals(clean, StringComparison.OrdinalIgnoreCase)))
                        {
                            missingNames.Add(clean);
                        }
                    }
                }
            }

            Dictionary<long, HeadshotEntry> metaById = new Dictionary<long, HeadshotEntry>();

            try
            {
                for (int i = 0; i < missingNames.Count; i += 50)
                {
                    List<string> chunk = missingNames.Skip(i).Take(50).ToList();
                    Dictionary<string, object> body = new Dictionary<string, object>();
                    body["usernames"] = chunk.ToArray();
                    body["excludeBannedUsers"] = false;
                    ApiResult r = await Req(HttpMethod.Post, "https://users.roblox.com/v1/usernames/users", null, Json.To(body), null, null);
                    if (!r.Ok) continue;
                    foreach (object d in Json.Arr(Json.Get(Json.Parse(r.Body), "data")))
                    {
                        long id = Json.Long(d, "id");
                        if (id <= 0) continue;
                        targetIds.Add(id);
                        HeadshotEntry he = new HeadshotEntry();
                        he.UserId = id;
                        he.Username = Json.Str(d, "name");
                        if (string.IsNullOrEmpty(he.Username)) he.Username = Json.Str(d, "requestedUsername");
                        he.DisplayName = Json.Str(d, "displayName");
                        if (string.IsNullOrEmpty(he.DisplayName)) he.DisplayName = he.Username;
                        metaById[id] = he;
                    }
                }

                List<long> idList = targetIds.Where(x => x > 0).ToList();
                Dictionary<long, string> thumbById = new Dictionary<long, string>();
                for (int i = 0; i < idList.Count; i += 50)
                {
                    List<long> chunk = idList.Skip(i).Take(50).ToList();
                    string joined = string.Join(",", chunk.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray());
                    string url = "https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=" + joined + "&size=150x150&format=Png&isCircular=false";
                    ApiResult r = await Req(HttpMethod.Get, url, null, null, null, null);
                    if (!r.Ok) continue;
                    foreach (object item in Json.Arr(Json.Get(Json.Parse(r.Body), "data")))
                    {
                        long tid = Json.Long(item, "targetId");
                        string img = Json.Str(item, "imageUrl");
                        if (tid > 0 && !string.IsNullOrEmpty(img)) thumbById[tid] = img;
                    }
                }

                long exp = DateTime.UtcNow.AddMinutes(15).Ticks;
                foreach (long id in idList)
                {
                    HeadshotEntry he;
                    if (!metaById.TryGetValue(id, out he))
                    {
                        he = new HeadshotEntry();
                        he.UserId = id;
                    }
                    string img;
                    if (thumbById.TryGetValue(id, out img)) he.HeadshotUrl = img;
                    if (string.IsNullOrEmpty(he.HeadshotUrl) && string.IsNullOrEmpty(he.Username)) continue;

                    lock (CacheLock)
                    {
                        CachedHeadshot ch = new CachedHeadshot();
                        ch.Entry = he;
                        ch.ExpiresTicks = exp;
                        HeadshotCache["id:" + id.ToString(CultureInfo.InvariantCulture)] = ch;
                        if (!string.IsNullOrEmpty(he.Username))
                            HeadshotCache["name:" + he.Username.ToLowerInvariant()] = ch;
                    }

                    res[id.ToString(CultureInfo.InvariantCulture)] = he;
                    if (!string.IsNullOrEmpty(he.Username))
                        res[he.Username.ToLowerInvariant()] = he;
                }
            }
            catch (Exception ex) { Log.Write("Headshot lookup failed: " + ex.Message); }

            return res;
        }

        static async Task<List<DirGameCandidate>> GetGameDirectoryAsync()
        {
            long now = DateTime.UtcNow.Ticks;
            lock (CacheLock)
            {
                if (CachedDirectory != null && CachedDirectory.Count > 0 && DirectoryExpiresTicks > now)
                    return new List<DirGameCandidate>(CachedDirectory);
            }

            Dictionary<long, DirGameCandidate> byPlace = new Dictionary<long, DirGameCandidate>();
            List<long> trendingOrder = new List<long>();
            string sid = "v9-" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);

            Task<ApiResult> tPlay = Req(HttpMethod.Get, "https://apis.roblox.com/explore-api/v1/get-sort-content?sessionId=" + Uri.EscapeDataString(sid) + "&sortId=top-playing-now", null, null, null, null);
            Task<ApiResult> tTrend = Req(HttpMethod.Get, "https://apis.roblox.com/explore-api/v1/get-sort-content?sessionId=" + Uri.EscapeDataString(sid) + "&sortId=top-trending", null, null, null, null);
            Task<ApiResult> tRoli = Req(HttpMethod.Get, "https://api.rolimons.com/games/v1/gamelist", null, null, null, null);

            await Task.WhenAll(tPlay, tTrend, tRoli);

            Action<ApiResult, bool> ingestExplore = delegate(ApiResult ar, bool trackOrder)
            {
                if (ar == null || !ar.Ok) return;
                object parsed = Json.Parse(ar.Body);
                foreach (object g in Json.Arr(Json.Get(parsed, "games")))
                {
                    long pid = Json.Long(g, "rootPlaceId");
                    string nm = Json.Str(g, "name");
                    if (pid <= 0 || string.IsNullOrEmpty(nm)) continue;
                    if (trackOrder && !trendingOrder.Contains(pid)) trendingOrder.Add(pid);
                    DirGameCandidate cur;
                    if (!byPlace.TryGetValue(pid, out cur))
                    {
                        cur = new DirGameCandidate();
                        cur.PlaceId = pid;
                        byPlace[pid] = cur;
                    }
                    long uid = Json.Long(g, "universeId");
                    if (uid > 0) cur.UniverseId = uid;
                    cur.Name = nm;
                    long pl = Json.Long(g, "playerCount");
                    if (pl > cur.Players) cur.Players = pl;
                }
            };

            ingestExplore(tPlay.Result, true);
            ingestExplore(tTrend.Result, true);

            ApiResult roliRes = tRoli.Result;
            if (roliRes != null && roliRes.Ok)
            {
                try
                {
                    object rObj = Json.Parse(roliRes.Body);
                    IDictionary<string, object> rGames = Json.Obj(Json.Get(rObj, "games"));
                    if (rGames != null)
                    {
                        foreach (KeyValuePair<string, object> kv in rGames)
                        {
                            long pid;
                            if (!long.TryParse(kv.Key, out pid) || pid <= 0) continue;
                            List<object> tuple = Json.Arr(kv.Value);
                            if (tuple.Count < 1) continue;
                            string nm = Convert.ToString(tuple[0], CultureInfo.InvariantCulture);
                            if (string.IsNullOrEmpty(nm)) continue;
                            long pl = 0;
                            if (tuple.Count >= 2 && tuple[1] != null)
                            {
                                try { pl = Convert.ToInt64(tuple[1], CultureInfo.InvariantCulture); }
                                catch (Exception) { }
                            }
                            string hint = tuple.Count >= 3 && tuple[2] != null ? Convert.ToString(tuple[2], CultureInfo.InvariantCulture) : "";
                            DirGameCandidate cur;
                            if (!byPlace.TryGetValue(pid, out cur))
                            {
                                cur = new DirGameCandidate();
                                cur.PlaceId = pid;
                                cur.Name = nm;
                                cur.Players = pl;
                                byPlace[pid] = cur;
                            }
                            if (string.IsNullOrEmpty(cur.IconHint) && hint.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                                cur.IconHint = hint;
                        }
                    }
                }
                catch (Exception) { }
            }

            List<DirGameCandidate> ordered = new List<DirGameCandidate>();
            HashSet<long> seen = new HashSet<long>();
            foreach (long pid in trendingOrder)
            {
                DirGameCandidate c;
                if (byPlace.TryGetValue(pid, out c) && seen.Add(pid)) ordered.Add(c);
            }
            foreach (DirGameCandidate c in byPlace.Values.OrderByDescending(x => x.Players))
            {
                if (seen.Add(c.PlaceId)) ordered.Add(c);
            }

            if (ordered.Count > 0)
            {
                lock (CacheLock)
                {
                    CachedDirectory = ordered;
                    DirectoryExpiresTicks = DateTime.UtcNow.AddMinutes(10).Ticks;
                }
            }
            return ordered;
        }

        static async Task<List<GameInfo>> HydrateGamesAsync(List<DirGameCandidate> candidates, string cookie)
        {
            List<GameInfo> results = new List<GameInfo>();
            if (candidates == null || candidates.Count == 0) return results;

            long now = DateTime.UtcNow.Ticks;
            Dictionary<long, GameInfo> byPlace = new Dictionary<long, GameInfo>();
            Dictionary<long, long> placeToUni = new Dictionary<long, long>();
            Dictionary<long, string> iconHints = new Dictionary<long, string>();
            List<DirGameCandidate> needUni = new List<DirGameCandidate>();

            foreach (DirGameCandidate c in candidates)
            {
                if (c.PlaceId <= 0) continue;
                if (!string.IsNullOrEmpty(c.IconHint)) iconHints[c.PlaceId] = c.IconHint;
                lock (CacheLock)
                {
                    CachedGame cg;
                    if (GameCache.TryGetValue(c.PlaceId, out cg) && cg.ExpiresTicks > now)
                    {
                        byPlace[c.PlaceId] = cg.Info;
                        continue;
                    }
                }
                if (c.UniverseId > 0) placeToUni[c.PlaceId] = c.UniverseId;
                else needUni.Add(c);
            }

            if (needUni.Count > 0)
            {
                List<Task<KeyValuePair<long, long>>> uTasks = new List<Task<KeyValuePair<long, long>>>();
                foreach (DirGameCandidate c in needUni)
                {
                    long localPid = c.PlaceId;
                    uTasks.Add(Task.Run(async delegate
                    {
                        ApiResult r = await Req(HttpMethod.Get, "https://apis.roblox.com/universes/v1/places/" + localPid + "/universe", null, null, null, null);
                        long uid = r.Ok ? Json.Long(Json.Parse(r.Body), "universeId") : 0;
                        return new KeyValuePair<long, long>(localPid, uid);
                    }));
                }
                KeyValuePair<long, long>[] pairs = await Task.WhenAll(uTasks);
                foreach (KeyValuePair<long, long> kv in pairs)
                {
                    if (kv.Value > 0) placeToUni[kv.Key] = kv.Value;
                }
            }

            List<long> uniIds = placeToUni.Values.Where(u => u > 0).Distinct().ToList();
            if (uniIds.Count > 0)
            {
                string uniParam = string.Join(",", uniIds.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray());
                Task<ApiResult> gTask = Req(HttpMethod.Get, "https://games.roblox.com/v1/games?universeIds=" + uniParam, cookie, null, null, null);
                Task<ApiResult> vTask = Req(HttpMethod.Get, "https://games.roblox.com/v1/games/votes?universeIds=" + uniParam, null, null, null, null);
                Task<ApiResult> iTask = Req(HttpMethod.Get, "https://thumbnails.roblox.com/v1/games/icons?universeIds=" + uniParam + "&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false", null, null, null, null);
                await Task.WhenAll(gTask, vTask, iTask);

                Dictionary<long, long> upMap = new Dictionary<long, long>();
                Dictionary<long, long> downMap = new Dictionary<long, long>();
                if (vTask.Result != null && vTask.Result.Ok)
                {
                    foreach (object v in Json.Arr(Json.Get(Json.Parse(vTask.Result.Body), "data")))
                    {
                        long uid = Json.Long(v, "id");
                        if (uid <= 0) continue;
                        upMap[uid] = Json.Long(v, "upVotes");
                        downMap[uid] = Json.Long(v, "downVotes");
                    }
                }

                Dictionary<long, string> iconMap = new Dictionary<long, string>();
                if (iTask.Result != null && iTask.Result.Ok)
                {
                    foreach (object ic in Json.Arr(Json.Get(Json.Parse(iTask.Result.Body), "data")))
                    {
                        long tid = Json.Long(ic, "targetId");
                        string img = Json.Str(ic, "imageUrl");
                        if (tid > 0 && !string.IsNullOrEmpty(img)) iconMap[tid] = img;
                    }
                }

                Dictionary<long, GameInfo> byUni = new Dictionary<long, GameInfo>();
                if (gTask.Result != null && gTask.Result.Ok)
                {
                    foreach (object g in Json.Arr(Json.Get(Json.Parse(gTask.Result.Body), "data")))
                    {
                        long uid = Json.Long(g, "id");
                        if (uid <= 0) continue;
                        GameInfo info = new GameInfo();
                        info.UniverseId = uid;
                        info.PlaceId = Json.Long(g, "rootPlaceId");
                        info.Name = Json.Str(g, "name");
                        object creatorObj = Json.Get(g, "creator");
                        info.Creator = Json.Str(creatorObj, "name");
                        if (string.IsNullOrEmpty(info.Creator)) info.Creator = "Roblox";
                        info.Genre = Json.Str(g, "genre_l1");
                        if (string.IsNullOrEmpty(info.Genre)) info.Genre = Json.Str(g, "genre");
                        if (string.IsNullOrEmpty(info.Genre)) info.Genre = "All";
                        info.Players = Json.Long(g, "playing");
                        info.Visits = Json.Long(g, "visits");
                        info.MaxPlayers = (int)Json.Long(g, "maxPlayers");
                        long up; long down;
                        upMap.TryGetValue(uid, out up);
                        downMap.TryGetValue(uid, out down);
                        info.UpVotes = up;
                        info.DownVotes = down;
                        long tot = up + down;
                        info.LikeRatio = tot > 0 ? (int)Math.Round((up * 100.0) / tot) : 0;
                        string iconUrl;
                        if (iconMap.TryGetValue(uid, out iconUrl)) info.IconUrl = iconUrl;
                        byUni[uid] = info;
                    }
                }

                long exp = DateTime.UtcNow.AddMinutes(5).Ticks;
                foreach (KeyValuePair<long, long> kv in placeToUni)
                {
                    GameInfo baseInfo;
                    if (!byUni.TryGetValue(kv.Value, out baseInfo)) continue;
                    GameInfo copy = new GameInfo();
                    copy.PlaceId = kv.Key;
                    copy.UniverseId = baseInfo.UniverseId;
                    copy.Name = string.IsNullOrEmpty(baseInfo.Name) ? ("Place " + kv.Key) : baseInfo.Name;
                    copy.Creator = baseInfo.Creator;
                    copy.Genre = baseInfo.Genre;
                    copy.Players = baseInfo.Players;
                    copy.Visits = baseInfo.Visits;
                    copy.MaxPlayers = baseInfo.MaxPlayers;
                    copy.UpVotes = baseInfo.UpVotes;
                    copy.DownVotes = baseInfo.DownVotes;
                    copy.LikeRatio = baseInfo.LikeRatio;
                    copy.IconUrl = !string.IsNullOrEmpty(baseInfo.IconUrl) ? baseInfo.IconUrl : (iconHints.ContainsKey(kv.Key) ? iconHints[kv.Key] : "");
                    byPlace[kv.Key] = copy;
                    lock (CacheLock)
                    {
                        CachedGame cg = new CachedGame();
                        cg.Info = copy;
                        cg.ExpiresTicks = exp;
                        GameCache[kv.Key] = cg;
                    }
                }
            }

            HashSet<long> added = new HashSet<long>();
            foreach (DirGameCandidate c in candidates)
            {
                GameInfo gi;
                if (byPlace.TryGetValue(c.PlaceId, out gi) && added.Add(c.PlaceId)) results.Add(gi);
            }
            return results;
        }

        public static async Task<GameInfo> ResolveGameAsync(long placeId, string cookie)
        {
            if (placeId <= 0) return null;
            try
            {
                DirGameCandidate cand = new DirGameCandidate();
                cand.PlaceId = placeId;
                List<GameInfo> list = await HydrateGamesAsync(new List<DirGameCandidate> { cand }, cookie);
                if (list.Count > 0) return list[0];
            }
            catch (Exception ex) { Log.Write("Game resolve failed: " + ex.Message); }
            return null;
        }

        public static async Task<List<GameInfo>> SearchGames(string query, string cookie)
        {
            List<GameInfo> list = new List<GameInfo>();
            try
            {
                string q = (query ?? "").Trim();
                long directPid = Util.ParsePlaceId(q);
                if (directPid > 0)
                {
                    GameInfo one = await ResolveGameAsync(directPid, cookie);
                    if (one != null) list.Add(one);
                    return list;
                }

                List<DirGameCandidate> dir = await GetGameDirectoryAsync();
                List<DirGameCandidate> picked;
                if (string.IsNullOrEmpty(q))
                {
                    picked = dir.Take(14).ToList();
                }
                else
                {
                    string qLower = q.ToLowerInvariant();
                    string[] terms = qLower.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    List<DirGameCandidate> starts = new List<DirGameCandidate>();
                    List<DirGameCandidate> contains = new List<DirGameCandidate>();
                    foreach (DirGameCandidate item in dir)
                    {
                        string lower = (item.Name ?? "").ToLowerInvariant();
                        bool ok = true;
                        for (int i = 0; i < terms.Length; i++)
                        {
                            if (!lower.Contains(terms[i])) { ok = false; break; }
                        }
                        if (!ok) continue;
                        if (lower.StartsWith(qLower)) starts.Add(item);
                        else contains.Add(item);
                    }
                    picked = starts.Concat(contains).Take(16).ToList();
                }

                list = await HydrateGamesAsync(picked, cookie);
            }
            catch (Exception ex) { Log.Write("Game search failed: " + ex.Message); }
            return list;
        }

        // New style private-server links: https://www.roblox.com/share?code=XXXX&type=Server
        public static async Task<LaunchSpec> ResolveShare(string code, string cookie)
        {
            try
            {
                string clean = Regex.Replace(code, "[^A-Za-z0-9]", "");
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["linkId"] = clean;
                body["linkType"] = "Server";
                ApiResult r = await PostCsrf("https://apis.roblox.com/sharelinks/v1/resolve-link", cookie, Json.To(body), null);
                if (!r.Ok) return null;
                object d = Json.Get(Json.Parse(r.Body), "privateServerInviteData");
                LaunchSpec s = new LaunchSpec();
                s.Kind = "Private";
                s.PlaceId = Json.Long(d, "placeId");
                s.LinkCode = Json.Str(d, "linkCode");
                if (s.PlaceId > 0 && s.LinkCode.Length > 0) return s;
            }
            catch (Exception ex) { Log.Write("Share link resolve failed: " + ex.Message); }
            return null;
        }

        public static async Task<string> Details(Account a)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Username:      " + a.Username);
            sb.AppendLine("Display name:  " + a.DisplayName);
            sb.AppendLine("User ID:       " + a.UserId);
            try
            {
                ApiResult u = await Req(HttpMethod.Get, "https://users.roblox.com/v1/users/" + a.UserId, null, null, null, null);
                if (u.Ok)
                {
                    object j = Json.Parse(u.Body);
                    DateTime created;
                    if (DateTime.TryParse(Json.Str(j, "created"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out created))
                        sb.AppendLine("Account age:   " + (int)(DateTime.UtcNow - created).TotalDays + " days (created " + created.ToString("yyyy-MM-dd") + ")");
                    sb.AppendLine("Banned:        " + (Json.Bool(j, "isBanned") ? "yes" : "no"));
                }
                ApiResult e = await Req(HttpMethod.Get, "https://economy.roblox.com/v1/users/" + a.UserId + "/currency", a.Cookie, null, null, null);
                if (e.Ok) sb.AppendLine("Robux:         " + Json.Long(Json.Parse(e.Body), "robux"));
                else sb.AppendLine("Robux:         (could not load, HTTP " + (int)e.Status + ")");
                ApiResult p = await Req(HttpMethod.Get, "https://premiumfeatures.roblox.com/v1/users/" + a.UserId + "/validate-membership", a.Cookie, null, null, null);
                if (p.Ok) sb.AppendLine("Premium:       " + (p.Body.Trim().ToLowerInvariant() == "true" ? "yes" : "no"));
            }
            catch (Exception ex) { sb.AppendLine("(some details failed: " + ex.Message + ")"); }
            return sb.ToString();
        }
    }
}
