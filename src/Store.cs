using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MultiRLBX
{
    /// <summary>
    /// Thread-safe persistence and encryption manager for user settings (<c>settings.json</c>)
    /// and credential vaults (<c>accounts.dat</c>).
    /// Supports Windows DPAPI (<c>CurrentUser</c>) and portable PBKDF2 + AES-256-CBC + HMAC-SHA256 modes.
    /// </summary>
    public static class Store
    {
        public static string Dir;
        public static string SettingsPath;
        public static string AccountsPath;
        public static string BackupDir;
        public static AppSettings Settings = new AppSettings();
        public static List<Account> Accounts = new List<Account>();
        public static string Password;            // null = DPAPI mode
        public static bool Loaded;                // guards against overwriting an unread vault
        public static bool MigratedFromMultiHub;  // true when legacy %APPDATA%\RobloxMultiHub data was imported
        static readonly object StoreLock = new object();
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MultiRLBX.v1");
        static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("RobloxMultiHub.v1");

        /// <summary>Initializes the default per-user <c>%APPDATA%\MultiRLBX</c> directory.</summary>
        public static void Init() { Init(null); }

        /// <summary>Initializes storage paths and loads settings.</summary>
        /// <param name="dataDir">Explicit directory override (used by the elevated <c>--memhelper</c> process).</param>
        public static void Init(string dataDir)
        {
            Dir = !string.IsNullOrEmpty(dataDir) ? dataDir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiRLBX");
            Directory.CreateDirectory(Dir);
            BackupDir = Path.Combine(Dir, "Backups");
            SettingsPath = Path.Combine(Dir, "settings.json");
            AccountsPath = Path.Combine(Dir, "accounts.dat");
            Log.Init(Dir);
            if (dataDir == null) ImportFromRobloxMultiHub();   // the memory helper must not touch user data
            LoadSettings();
        }

        // First start after the rename: if this data folder is still empty and the old Roblox MultiHub
        // folder exists, its accounts, settings, backups and caches are copied over, so everything
        // continues where it left off. The old folder itself is never modified or deleted - removing
        // it later is up to you. Password-protected files keep working as they were (the password
        // does not change); DPAPI files decrypt through the legacy entropy above and are re-saved
        // with the new one on the first save.
        static void ImportFromRobloxMultiHub()
        {
            try
            {
                string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RobloxMultiHub");
                if (!Directory.Exists(old)) return;
                if (File.Exists(SettingsPath) || File.Exists(AccountsPath)) return;   // not a first start
                bool any = false;
                if (File.Exists(Path.Combine(old, "settings.json"))) { File.Copy(Path.Combine(old, "settings.json"), SettingsPath, true); any = true; }
                if (File.Exists(Path.Combine(old, "accounts.dat"))) { File.Copy(Path.Combine(old, "accounts.dat"), AccountsPath, true); any = true; }
                if (CopyDirFiles(Path.Combine(old, "Backups"), BackupDir, "accounts_*.dat")) any = true;
                if (CopyDirFiles(Path.Combine(old, "avatars"), Path.Combine(Dir, "avatars"), "*")) any = true;
                if (CopyDirFiles(Path.Combine(old, "lang"), Path.Combine(Dir, "lang"), "*")) any = true;
                MigratedFromMultiHub = any;
            }
            catch (Exception ex) { Log.Error("ImportFromRobloxMultiHub", ex); }
        }

        static bool CopyDirFiles(string from, string to, string pattern)
        {
            try
            {
                if (!Directory.Exists(from)) return false;
                string[] files = Directory.GetFiles(from, pattern);
                if (files.Length == 0) return false;
                Directory.CreateDirectory(to);
                foreach (string f in files) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
                return true;
            }
            catch (Exception) { return false; }
        }

        public static void LoadSettings()
        {
            lock (StoreLock)
            {
                try
                {
                    if (File.Exists(SettingsPath))
                    {
                        AppSettings s = Json.From<AppSettings>(File.ReadAllText(SettingsPath, Encoding.UTF8));
                        if (s != null) Settings = s;
                    }
                }
                catch (Exception ex) { Log.Error("LoadSettings", ex); }
                if (Settings.Layout == null) Settings.Layout = new LayoutSettings();
                if (Settings.Groups == null) Settings.Groups = new List<string>();
                if (Settings.Recent == null) Settings.Recent = new List<GameEntry>();
                if (Settings.Pinned == null) Settings.Pinned = new List<PinnedRect>();
                if (Settings.Presets == null) Settings.Presets = new List<Preset>();
                if (Settings.Macro == null) Settings.Macro = new MacroSettings();
                if (Settings.Mem == null) Settings.Mem = new MemSettings();
                Settings.Mem.Threshold = Math.Max(30, Math.Min(98, Settings.Mem.Threshold));
                if (string.IsNullOrEmpty(Settings.AntiAfkAction)) Settings.AntiAfkAction = "Nudge";
                if (Settings.StartupPreset == null) Settings.StartupPreset = "";
            }
        }

        public static void SaveSettings()
        {
            lock (StoreLock)
            {
                try { AtomicWrite(SettingsPath, Encoding.UTF8.GetBytes(Json.To(Settings))); }
                catch (Exception ex) { Log.Error("SaveSettings", ex); }
            }
        }

        /// <summary>Checks whether <c>accounts.dat</c> uses password-based AES encryption (<c>'P'</c> header byte).</summary>
        public static bool AccountsFileIsPasswordProtected()
        {
            if (!File.Exists(AccountsPath)) return false;
            byte[] b = File.ReadAllBytes(AccountsPath);
            return b.Length > 0 && b[0] == (byte)'P';
        }

        /// <summary>Decrypts and loads saved accounts into memory, creating a rolling backup.</summary>
        /// <param name="password">Vault password, or <c>null</c> for Windows DPAPI mode.</param>
        /// <returns><c>null</c> on success; otherwise a user-facing error message.</returns>
        public static string LoadAccounts(string password)
        {
            lock (StoreLock)
            {
                try
                {
                    if (!File.Exists(AccountsPath))
                    {
                        Accounts = new List<Account>();
                        Password = password;
                        Loaded = true;
                        return null;
                    }
                    byte[] raw = File.ReadAllBytes(AccountsPath);
                    string json = Decrypt(raw, password);
                    AccountFile f = Json.From<AccountFile>(json);
                    Accounts = (f != null && f.Accounts != null) ? f.Accounts : new List<Account>();
                    Password = password;
                    Loaded = true;
                    Backup();
                    return null;
                }
                catch (CryptographicException)
                {
                    if (password != null) return "Wrong password (or the file is damaged).";
                    return "This accounts file could not be decrypted. It was probably created by a different Windows user or PC.";
                }
                catch (Exception ex)
                {
                    Log.Error("LoadAccounts", ex);
                    return "Could not read the accounts file: " + ex.Message;
                }
            }
        }

        public static void StartFresh()
        {
            lock (StoreLock)
            {
                try { if (File.Exists(AccountsPath)) File.Copy(AccountsPath, AccountsPath + ".old", true); }
                catch (Exception) { }
                Accounts = new List<Account>();
                Password = null;
                Loaded = true;
                SaveAccounts();
            }
        }

        public static void SaveAccounts()
        {
            lock (StoreLock)
            {
                if (!Loaded) return;
                try
                {
                    AccountFile f = new AccountFile();
                    f.Accounts = Accounts;
                    byte[] enc = Encrypt(Encoding.UTF8.GetBytes(Json.To(f)), Password);
                    AtomicWrite(AccountsPath, enc);
                }
                catch (Exception ex) { Log.Error("SaveAccounts", ex); }
            }
        }

        public static void Backup()
        {
            try
            {
                if (!File.Exists(AccountsPath)) return;
                Directory.CreateDirectory(BackupDir);
                string name = "accounts_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".dat";
                File.Copy(AccountsPath, Path.Combine(BackupDir, name), true);
                string[] all = Directory.GetFiles(BackupDir, "accounts_*.dat");
                Array.Sort(all);
                for (int i = 0; i < all.Length - 10; i++) File.Delete(all[i]);
            }
            catch (Exception) { }
        }

        static void AtomicWrite(string path, byte[] data)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }

        static byte[] Rand(int n)
        {
            byte[] b = new byte[n];
            using (RNGCryptoServiceProvider r = new RNGCryptoServiceProvider()) { r.GetBytes(b); }
            return b;
        }

        /// <summary>Encrypts UTF-8 payload bytes using DPAPI (<c>'D'</c>) or PBKDF2 + AES-256-CBC + HMAC-SHA256 (<c>'P'</c>).</summary>
        public static byte[] Encrypt(byte[] data, string password)
        {
            if (password == null)
            {
                byte[] enc = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
                byte[] outD = new byte[enc.Length + 1];
                outD[0] = (byte)'D';
                Buffer.BlockCopy(enc, 0, outD, 1, enc.Length);
                return outD;
            }
            byte[] salt = Rand(16);
            byte[] iv = Rand(16);
            byte[] keys = new Rfc2898DeriveBytes(password, salt, 100000).GetBytes(64);
            byte[] encKey = new byte[32];
            byte[] macKey = new byte[32];
            Buffer.BlockCopy(keys, 0, encKey, 0, 32);
            Buffer.BlockCopy(keys, 32, macKey, 0, 32);
            byte[] cipher;
            using (Aes aes = Aes.Create())
            {
                aes.Key = encKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using (MemoryStream ms = new MemoryStream())
                {
                    using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        cs.Write(data, 0, data.Length);
                        cs.FlushFinalBlock();
                    }
                    cipher = ms.ToArray();
                }
            }
            byte[] body = new byte[16 + 16 + cipher.Length];
            Buffer.BlockCopy(salt, 0, body, 0, 16);
            Buffer.BlockCopy(iv, 0, body, 16, 16);
            Buffer.BlockCopy(cipher, 0, body, 32, cipher.Length);
            byte[] mac;
            using (HMACSHA256 h = new HMACSHA256(macKey)) { mac = h.ComputeHash(body); }
            byte[] result = new byte[1 + 32 + body.Length];
            result[0] = (byte)'P';
            Buffer.BlockCopy(mac, 0, result, 1, 32);
            Buffer.BlockCopy(body, 0, result, 33, body.Length);
            return result;
        }

        public static string Decrypt(byte[] raw, string password)
        {
            if (raw.Length < 2) throw new CryptographicException("empty");
            if (raw[0] == (byte)'D')
            {
                byte[] enc = new byte[raw.Length - 1];
                Buffer.BlockCopy(raw, 1, enc, 0, enc.Length);
                byte[] data;
                try { data = ProtectedData.Unprotect(enc, Entropy, DataProtectionScope.CurrentUser); }
                catch (CryptographicException)
                {
                    // File saved by Roblox MultiHub before the rename - same Windows user, old entropy.
                    data = ProtectedData.Unprotect(enc, LegacyEntropy, DataProtectionScope.CurrentUser);
                }
                return Encoding.UTF8.GetString(data);
            }
            if (raw[0] == (byte)'P')
            {
                if (password == null) throw new CryptographicException("password needed");
                if (raw.Length < 1 + 32 + 32 + 16) throw new CryptographicException("short");
                byte[] mac = new byte[32];
                Buffer.BlockCopy(raw, 1, mac, 0, 32);
                byte[] body = new byte[raw.Length - 33];
                Buffer.BlockCopy(raw, 33, body, 0, body.Length);
                byte[] salt = new byte[16];
                byte[] iv = new byte[16];
                Buffer.BlockCopy(body, 0, salt, 0, 16);
                Buffer.BlockCopy(body, 16, iv, 0, 16);
                byte[] keys = new Rfc2898DeriveBytes(password, salt, 100000).GetBytes(64);
                byte[] encKey = new byte[32];
                byte[] macKey = new byte[32];
                Buffer.BlockCopy(keys, 0, encKey, 0, 32);
                Buffer.BlockCopy(keys, 32, macKey, 0, 32);
                byte[] check;
                using (HMACSHA256 h = new HMACSHA256(macKey)) { check = h.ComputeHash(body); }
                int diff = 0;
                for (int i = 0; i < 32; i++) diff |= mac[i] ^ check[i];
                if (diff != 0) throw new CryptographicException("bad mac");
                using (Aes aes = Aes.Create())
                {
                    aes.Key = encKey;
                    aes.IV = iv;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
                        {
                            cs.Write(body, 32, body.Length - 32);
                            cs.FlushFinalBlock();
                        }
                        return Encoding.UTF8.GetString(ms.ToArray());
                    }
                }
            }
            throw new CryptographicException("unknown format");
        }

        // ---- helpers used by the UI ----
        public static void RenumberOrder()
        {
            for (int i = 0; i < Accounts.Count; i++) Accounts[i].Order = i;
        }

        public static List<string> AllGroups()
        {
            List<string> g = new List<string>(Settings.Groups);
            foreach (Account a in Accounts)
                if (!string.IsNullOrEmpty(a.Group) && !g.Contains(a.Group)) g.Add(a.Group);
            g.Sort(StringComparer.OrdinalIgnoreCase);
            return g;
        }

        public static Account FindByCookie(string cookie)
        {
            return Accounts.FirstOrDefault(a => a.Cookie == cookie);
        }

        /// <summary>
        /// Exports all saved accounts and settings into a portable, password-protected <c>.v9vault</c> JSON envelope.
        /// Uses PBKDF2 + AES-256-CBC + HMAC-SHA256 when a non-empty password is supplied.
        /// </summary>
        public static string ExportVaultString(string password)
        {
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("Podaj haslo szyfrujace kopie .v9vault.");
            lock (StoreLock)
            {
                Dictionary<string, object> inner = new Dictionary<string, object>();
                inner["magic"] = "v9vault";
                inner["version"] = 1;
                inner["author"] = "v9pv";
                inner["exportedAt"] = DateTime.UtcNow.ToString("o");
                inner["accounts"] = Accounts;
                inner["settings"] = Settings;

                byte[] plain = Encoding.UTF8.GetBytes(Json.To(inner));
                byte[] cipher = Encrypt(plain, password);

                Dictionary<string, object> envelope = new Dictionary<string, object>();
                envelope["format"] = "v9vault";
                envelope["v"] = 1;
                envelope["author"] = "v9pv";
                envelope["payload"] = Convert.ToBase64String(cipher);
                return Json.To(envelope);
            }
        }

        /// <summary>
        /// Decrypts and restores accounts and settings from a <c>.v9vault</c> backup payload.
        /// </summary>
        public static void ImportVaultString(string raw, string password, out int importedAccounts)
        {
            importedAccounts = 0;
            if (string.IsNullOrWhiteSpace(raw)) throw new ArgumentException("Pusty plik .v9vault.");
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("Podaj haslo do odszyfrowania .v9vault.");

            object env = Json.Parse(raw);
            string b64 = Json.Str(env, "payload");
            if (string.IsNullOrEmpty(b64)) throw new ArgumentException("Niepoprawny format kopii .v9vault.");
            byte[] cipher = Convert.FromBase64String(b64);
            string json = Decrypt(cipher, password);

            object inner = Json.Parse(json);
            if (Json.Str(inner, "magic") != "v9vault") throw new ArgumentException("Uszkodzony naglowek kopii .v9vault.");

            lock (StoreLock)
            {
                string accJson = Json.To(new Dictionary<string, object> { { "Accounts", Json.Get(inner, "accounts") } });
                AccountFile af = Json.From<AccountFile>(accJson);
                if (af != null && af.Accounts != null)
                {
                    foreach (Account a in af.Accounts)
                    {
                        if (a == null || string.IsNullOrEmpty(a.Cookie)) continue;
                        Account existing = Accounts.FirstOrDefault(x =>
                            (!string.IsNullOrEmpty(a.Id) && x.Id == a.Id) ||
                            (a.UserId > 0 && x.UserId == a.UserId) ||
                            x.Cookie == a.Cookie);
                        if (existing != null)
                        {
                            existing.Cookie = a.Cookie;
                            if (!string.IsNullOrEmpty(a.Alias)) existing.Alias = a.Alias;
                            if (!string.IsNullOrEmpty(a.Group)) existing.Group = a.Group;
                            if (!string.IsNullOrEmpty(a.Tags)) existing.Tags = a.Tags;
                            if (!string.IsNullOrEmpty(a.SavedPlaceId)) existing.SavedPlaceId = a.SavedPlaceId;
                            if (!string.IsNullOrEmpty(a.SavedJobId)) existing.SavedJobId = a.SavedJobId;
                            existing.FpsCap = a.FpsCap;
                        }
                        else
                        {
                            if (string.IsNullOrEmpty(a.Id)) a.Id = Guid.NewGuid().ToString("N");
                            a.Order = Accounts.Count;
                            Accounts.Add(a);
                        }
                        importedAccounts++;
                    }
                }

                object setObj = Json.Get(inner, "settings");
                if (setObj != null)
                {
                    AppSettings restored = Json.From<AppSettings>(Json.To(setObj));
                    if (restored != null)
                    {
                        Settings = restored;
                        SaveSettings();
                    }
                }
                Loaded = true;
                SaveAccounts();
            }
        }
    }
}
