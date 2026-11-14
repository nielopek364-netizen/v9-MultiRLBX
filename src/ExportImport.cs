using System;
using System.Collections.Generic;

namespace MultiRLBX
{
    /// <summary>
    /// Parses bulk credential lines (<c>nick:password:cookie</c> or raw <c>.ROBLOSECURITY</c> tokens)
    /// submitted through the desktop web UI.
    /// </summary>
    public static class ExportImport
    {
        /// <summary>Normalized record produced from a single bulk-import line.</summary>
        public class BulkRow
        {
            public string User = "";
            public string Pass = "";
            public string Cookie = "";
        }

        /// <summary>Extracts valid <see cref="BulkRow"/> entries from multi-line input text.</summary>
        public static List<BulkRow> ParseBulk(string raw)
        {
            List<BulkRow> rows = new List<BulkRow>();
            if (string.IsNullOrEmpty(raw)) return rows;
            foreach (string line0 in raw.Replace("\r\n", "\n").Split('\n'))
            {
                string line = line0.Trim();
                if (line.Length == 0) continue;
                int cs = line.IndexOf("_|WARNING:", StringComparison.Ordinal);
                if (cs < 0) continue;
                string cookie = line.Substring(cs).Trim();
                if (Util.ExtractCookies(cookie).Count == 0) continue;
                BulkRow r = new BulkRow();
                r.Cookie = cookie;
                string head = line.Substring(0, cs).Trim();
                if (head.Length > 0)
                {
                    int sep = head.IndexOf(':');
                    if (sep > 0)
                    {
                        r.User = head.Substring(0, sep).Trim();
                        r.Pass = head.Substring(sep + 1).Trim();
                        if (r.Pass.StartsWith(":")) r.Pass = r.Pass.Substring(1).Trim();
                    }
                    else r.User = head;
                }
                rows.Add(r);
            }
            return rows;
        }

        /// <summary>Extracts <c>.ROBLOSECURITY</c> cookies from Cookie-Editor / EditThisCookie JSON payloads.</summary>
        public static List<string> ParseJsonCookies(string raw)
        {
            List<string> found = new List<string>();
            if (string.IsNullOrWhiteSpace(raw)) return found;
            try
            {
                object parsed = Json.Parse(raw);
                CollectJsonCookies(parsed, found);
            }
            catch (Exception) { }
            if (found.Count == 0)
            {
                foreach (string ck in Util.ExtractCookies(raw))
                    if (!found.Contains(ck)) found.Add(ck);
            }
            return found;
        }

        static void CollectJsonCookies(object node, List<string> found)
        {
            if (node == null) return;
            IDictionary<string, object> d = Json.Obj(node);
            if (d != null)
            {
                string name = Json.Str(d, "name");
                string val = Json.Str(d, "value");
                if (name == ".ROBLOSECURITY" && !string.IsNullOrEmpty(val) && !found.Contains(val))
                {
                    found.Add(val);
                    return;
                }
                object direct;
                if (d.TryGetValue(".ROBLOSECURITY", out direct) && direct is string)
                {
                    string sv = ((string)direct).Trim();
                    if (!string.IsNullOrEmpty(sv) && !found.Contains(sv)) found.Add(sv);
                }
                foreach (KeyValuePair<string, object> kv in d)
                    if (kv.Value != null && !(kv.Value is string)) CollectJsonCookies(kv.Value, found);
                return;
            }
            foreach (object item in Json.Arr(node)) CollectJsonCookies(item, found);
        }
    }
}
