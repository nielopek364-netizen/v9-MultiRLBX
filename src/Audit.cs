using System;
using System.IO;

namespace MultiRLBX
{
    /// <summary>
    /// Append-only plain-text audit logger recording security and lifecycle events
    /// (launches, stops, account edits, and cookie exports).
    /// </summary>
    public static class Audit
    {
        static readonly object gate = new object();

        /// <summary>Full path to <c>audit.log</c> inside the active application data directory.</summary>
        public static string FilePath { get { return Path.Combine(Store.Dir, "audit.log"); } }

        /// <summary>Appends a timestamped audit entry in a thread-safe manner.</summary>
        /// <param name="action">Concise event description.</param>
        public static void Write(string action)
        {
            try
            {
                lock (gate)
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + action + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }
}
