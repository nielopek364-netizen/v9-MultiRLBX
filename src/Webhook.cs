using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MultiRLBX
{
    /// <summary>
    /// Optional outgoing webhook alert dispatcher (crash, relaunch, HTTP 403 lock, RAM clean, daily summary).
    /// Messages are serialized through a <see cref="SemaphoreSlim"/> queue with HTTP 429 Retry-After backoff.
    /// </summary>
    public static class Webhook
    {
        public const int Blurple = 0x5865F2;
        public const int Green   = 0x16A34A;
        public const int Amber   = 0xD97706;
        public const int Yellow  = 0xD97706;
        public const int Red     = 0xDC2626;
        public const int Cyan    = 0x0891B2;

        static readonly HttpClient Http = new HttpClient();
        static readonly SemaphoreSlim QueueLock = new SemaphoreSlim(1, 1);

        static Webhook()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            Http.Timeout = TimeSpan.FromSeconds(10);
        }

        /// <summary><c>true</c> when webhook alerts are enabled and at least one valid HTTPS endpoint is configured.</summary>
        public static bool Enabled
        {
            get
            {
                return Store.Settings.Webhook &&
                    (LooksLikeWebhook(Store.Settings.WebhookUrl) || LooksLikeWebhook(Store.Settings.WebhookStatusUrl));
            }
        }

        /// <summary>Validates that the supplied URL is a well-formed HTTPS webhook endpoint.</summary>
        public static bool LooksLikeWebhook(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            string u = url.Trim();
            return u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && u.IndexOf("/api/webhooks/", StringComparison.OrdinalIgnoreCase) > 0;
        }

        static string UrlFor(bool status)
        {
            if (status && LooksLikeWebhook(Store.Settings.WebhookStatusUrl))
                return Store.Settings.WebhookStatusUrl;
            return Store.Settings.WebhookUrl;
        }

        /// <summary>Queues a status-channel alert using <c>WebhookStatusUrl</c> if configured.</summary>
        public static void SendStatus(string title, string desc, int color)
        {
            if (!Enabled) return;
            Post(UrlFor(true), title, desc, color, null, false);
        }

        /// <summary>Queues a status-channel alert with structured key/value fields.</summary>
        public static void SendStatusFields(string title, string desc, int color, string[] fieldPairs)
        {
            if (!Enabled) return;
            Post(UrlFor(true), title, desc, color, fieldPairs, false);
        }

        /// <summary>Queues a standard alert embed.</summary>
        public static void Send(string title, string desc, int color)
        {
            SendFields(title, desc, color, null, false);
        }

        /// <summary>Queues a standard alert embed with structured key/value fields.</summary>
        public static void SendFields(string title, string desc, int color, string[] fieldPairs)
        {
            SendFields(title, desc, color, fieldPairs, false);
        }

        /// <summary>Queues a critical alert embed (optionally prepending <c>@here</c> if enabled).</summary>
        public static void SendCritical(string title, string desc, int color, string[] fieldPairs)
        {
            SendFields(title, desc, color, fieldPairs, true);
        }

        static void SendFields(string title, string desc, int color, string[] fieldPairs, bool critical)
        {
            if (!Enabled) return;
            Post(UrlFor(false), title, desc, color, fieldPairs, critical);
        }

        /// <summary>Sends a test or targeted alert directly to a specific webhook URL.</summary>
        public static void SendTo(string url, string title, string desc, int color)
        {
            Post(url, title, desc, color, null, false);
        }

        /// <summary>Sends a targeted alert with key/value fields directly to a specific webhook URL.</summary>
        public static void SendTo(string url, string title, string desc, int color, string[] fieldPairs)
        {
            Post(url, title, desc, color, fieldPairs, false);
        }

        static void Post(string url, string title, string desc, int color, string[] fieldPairs, bool critical)
        {
            if (!LooksLikeWebhook(url)) return;
            string targetUrl = url.Trim();
            Task.Run(async delegate
            {
                await QueueLock.WaitAsync();
                try
                {
                    Dictionary<string, object> embed = new Dictionary<string, object>();
                    embed["title"] = title ?? "";
                    embed["description"] = desc ?? "";
                    embed["color"] = color;
                    embed["timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                    if (fieldPairs != null && fieldPairs.Length >= 2)
                    {
                        List<object> fs = new List<object>();
                        for (int i = 0; i + 1 < fieldPairs.Length; i += 2)
                        {
                            if (string.IsNullOrEmpty(fieldPairs[i + 1])) continue;
                            Dictionary<string, object> f = new Dictionary<string, object>();
                            f["name"] = fieldPairs[i];
                            f["value"] = fieldPairs[i + 1];
                            f["inline"] = true;
                            fs.Add(f);
                        }
                        if (fs.Count > 0) embed["fields"] = fs;
                    }
                    Dictionary<string, object> foot = new Dictionary<string, object>();
                    foot["text"] = "v9 MultiRLBX v1.0.0 · by v9pv";
                    embed["footer"] = foot;

                    Dictionary<string, object> body = new Dictionary<string, object>();
                    string name = (Store.Settings.WebhookName ?? "").Trim();
                    body["username"] = name.Length == 0 ? "v9 MultiRLBX" : name;
                    string avatar = (Store.Settings.WebhookAvatar ?? "").Trim();
                    if (avatar.Length > 0) body["avatar_url"] = avatar;
                    if (critical && Store.Settings.WebhookHere) body["content"] = "@here";
                    body["embeds"] = new object[] { embed };

                    string payload = Json.To(body);
                    HttpResponseMessage r = await Http.PostAsync(targetUrl, new StringContent(payload, Encoding.UTF8, "application/json"));
                    if ((int)r.StatusCode == 429)
                    {
                        int waitMs = 1800;
                        IEnumerable<string> vals;
                        if (r.Headers.TryGetValues("Retry-After", out vals))
                        {
                            foreach (string v in vals)
                            {
                                double sec;
                                if (double.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out sec))
                                {
                                    waitMs = Math.Max(500, Math.Min(10000, (int)(sec * 1000) + 150));
                                    break;
                                }
                            }
                        }
                        await Task.Delay(waitMs);
                        await Http.PostAsync(targetUrl, new StringContent(payload, Encoding.UTF8, "application/json"));
                    }
                }
                catch (Exception ex) { Log.Error("Webhook", ex); }
                finally { QueueLock.Release(); }
            });
        }
    }
}
