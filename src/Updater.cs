using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

namespace MultiRLBX
{
    /// <summary>
    /// Represents build version and GitHub release metadata reported to the UI and system tray.
    /// </summary>
    public class UpdateInfo
    {
        public string Current = "1.0.0";
        public string Latest = "1.0.0";
        public bool Newer = false;
        public string Notes = "";
        public string DownloadUrl = "";
        public string Error = "";
    }

    /// <summary>
    /// Version reporter and on-demand GitHub Releases checker targeting <c>nielopek364-netizen</c>.
    /// </summary>
    public static class Updater
    {
        public const string Author = "v9pv";
        public const string GithubUrl = "https://github.com/nielopek364-netizen/";
        public const string ReleasesUrl = "https://github.com/nielopek364-netizen/v9-MultiRLBX/releases";
        public const string RepoApiUrl = "https://api.github.com/repos/nielopek364-netizen/v9-MultiRLBX/releases/latest";

        /// <summary>Returns the semantic informational version embedded in the compiled assembly.</summary>
        public static string CurrentVersion()
        {
            try
            {
                object[] a = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                if (a.Length > 0) return ((AssemblyInformationalVersionAttribute)a[0]).InformationalVersion;
            }
            catch (Exception) { }
            return "1.0.0";
        }

        /// <summary>Builds a local <see cref="UpdateInfo"/> snapshot matching the current build version.</summary>
        public static UpdateInfo CheckLocal()
        {
            UpdateInfo info = new UpdateInfo();
            info.Current = CurrentVersion();
            info.Latest = info.Current;
            info.Newer = false;
            info.DownloadUrl = ReleasesUrl;
            return info;
        }

        /// <summary>Queries GitHub Releases on demand to check if a newer release is published.</summary>
        public static async Task<UpdateInfo> CheckGitHubAsync()
        {
            UpdateInfo info = CheckLocal();
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(8);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("v9-MultiRLBX/" + info.Current);
                    using (HttpResponseMessage resp = await client.GetAsync(RepoApiUrl))
                    {
                        if ((int)resp.StatusCode == 404)
                        {
                            info.Latest = info.Current;
                            info.Newer = false;
                            info.Notes = "Brak opublikowanych wydan na GitHub Releases (wersja lokalna jest aktualna).";
                            return info;
                        }
                        if (!resp.IsSuccessStatusCode)
                        {
                            info.Error = "GitHub API HTTP " + (int)resp.StatusCode;
                            return info;
                        }
                        string raw = await resp.Content.ReadAsStringAsync();
                        object j = Json.Parse(raw);
                        string tag = Json.Str(j, "tag_name").Trim().TrimStart('v', 'V');
                        if (!string.IsNullOrEmpty(tag)) info.Latest = tag;
                        string htmlUrl = Json.Str(j, "html_url");
                        if (!string.IsNullOrEmpty(htmlUrl)) info.DownloadUrl = htmlUrl;
                        info.Notes = Util.Trunc(Json.Str(j, "body"), 240);
                        info.Newer = IsNewer(info.Latest, info.Current);
                    }
                }
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }
            return info;
        }

        static bool IsNewer(string latest, string current)
        {
            try
            {
                Version vLat, vCur;
                if (Version.TryParse(latest, out vLat) && Version.TryParse(current, out vCur))
                    return vLat > vCur;
            }
            catch (Exception) { }
            return false;
        }
    }
}
