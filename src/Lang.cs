using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MultiRLBX
{
    // Built-in Polish (default) and English translation support.
    // Custom language packs can be added to <data folder>\lang\<code>.json.
    public static class Lang
    {
        public static string Current = "pl";
        static readonly Dictionary<string, string> map = new Dictionary<string, string>();

        public static List<string[]> Available()
        {
            List<string[]> list = new List<string[]>();
            list.Add(new string[] { "pl", "Polski (Polish)" });
            list.Add(new string[] { "en", "English" });
            try
            {
                string dir = Path.Combine(Store.Dir, "lang");
                if (Directory.Exists(dir))
                    foreach (string f in Directory.GetFiles(dir, "*.json"))
                    {
                        string code = Path.GetFileNameWithoutExtension(f);
                        if (code == "pl" || code == "en") continue;
                        list.Add(new string[] { code, code });
                    }
            }
            catch (Exception) { }
            return list;
        }

        public static void Init() { Load(Store.Settings.Language); }

        public static void Load(string code)
        {
            Current = code == "en" ? "en" : "pl";
            map.Clear();
            if (Current == "pl") foreach (KeyValuePair<string, string> kv in Polish) map[kv.Key] = kv.Value;

            try
            {
                string f = Path.Combine(Store.Dir, "lang", Current + ".json");
                if (File.Exists(f))
                {
                    object o = Json.Parse(File.ReadAllText(f, Encoding.UTF8));
                    IDictionary<string, object> d = Json.Obj(o);
                    if (d != null) foreach (KeyValuePair<string, object> kv in d) map[kv.Key] = Convert.ToString(kv.Value);
                }
            }
            catch (Exception ex) { Log.Error("Lang.Load", ex); }
        }

        public static string T(string english)
        {
            if (Current == "en" || string.IsNullOrEmpty(english)) return english;
            string v;
            return map.TryGetValue(english, out v) && !string.IsNullOrEmpty(v) ? v : english;
        }

        static readonly Dictionary<string, string> Polish = new Dictionary<string, string>
        {
            { "Open Hub", "Otwórz panel" },
            { "Open in browser", "Otwórz w przeglądarce" },
            { "Copy web URL", "Kopiuj adres Web" },
            { "Copied web URL", "Skopiowano adres Web do schowka." },
            { "Launch all", "Uruchom wszystkie" },
            { "Stop all", "Zatrzymaj wszystkie" },
            { "Clean RAM", "Wyczyść RAM" },
            { "Macro Recorder (.v9m)", "Nagrywarka Makr (.v9m)" },
            { "Watchdog: ON", "Watchdog: WŁ" },
            { "Watchdog: OFF", "Watchdog: WYŁ" },
            { "Exit", "Zakończ" },
            { "Running", "Aktywne" },
            { "Windows", "Okna" },
            { "Accounts", "Konta" },
            { "Unlock", "Odblokuj" },
            { "Wrong password.", "Błędne hasło." }
        };
    }
}
