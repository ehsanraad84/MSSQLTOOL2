using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

namespace MSSQLTool
{
    internal enum RuntimeLogLevel
    {
        Info,
        Warn,
        Error
    }

    internal sealed class RuntimeLogEntry
    {
        public DateTime Time { get; set; }
        public RuntimeLogLevel Level { get; set; }
        public string Category { get; set; }
        public string Message { get; set; }
        public int ThreadId { get; set; }

        /// <summary>One line: time, level, category, thread and the message. The same text goes to the
        /// diagnostics page and to the log file, so a report can be matched line by line.</summary>
        public string Format()
        {
            return Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + " [" + Level.ToString().ToUpperInvariant() + "]"
                + " " + (Category ?? "?")
                + " #" + ThreadId.ToString(CultureInfo.InvariantCulture)
                + " " + (Message ?? string.Empty);
        }

        public override string ToString() => Format();
    }

    /// <summary>
    /// Test-mode log: off by default, switched on from Diagnostics in the settings window.  While it
    /// is on, the extension records what the completion pipeline decided (request scope, metadata
    /// load, filtering, popup show/hide, focus) with timings, and the diagnostics page shows the
    /// lines as they are produced.  Everything is gated by <see cref="Enabled"/>: the disabled path
    /// is a single volatile read, so leaving test mode off costs nothing per keystroke.
    /// </summary>
    internal static class RuntimeLog
    {
        public const int Capacity = 4000;

        private static readonly object Sync = new object();
        private static readonly List<RuntimeLogEntry> Entries = new List<RuntimeLogEntry>(256);
        private static readonly Stopwatch SessionClock = Stopwatch.StartNew();
        private static int enabled;
        private static int detailed;

        static RuntimeLog()
        {
            // The switches are persisted with the SQL completion settings, so a test session survives
            // a restart until it is switched off again.
            try
            {
                SettingsManager.SqlCompletionSettings settings = SettingsManager.GetSqlCompletionSettings();
                enabled = settings?.testMode == true ? 1 : 0;
                detailed = settings?.detailedCompletionLog == true ? 1 : 0;
            }
            catch (Exception) { enabled = 0; detailed = 0; }
        }

        /// <summary>Raised for every recorded line, on the thread that wrote it.</summary>
        public static event Action<RuntimeLogEntry> Logged;

        public static bool Enabled
        {
            get => Volatile.Read(ref enabled) == 1;
            set
            {
                int next = value ? 1 : 0;
                int previous = Interlocked.Exchange(ref enabled, next);
                if (previous == next) return;
                if (value) Write("SESSION", "test mode enabled: " + DescribeEnvironment());
                else Write("SESSION", "test mode disabled");
            }
        }

        /// <summary>
        /// Detailed autocomplete log (Diagnostics page).  It is meaningful only together with
        /// <see cref="Enabled"/> and adds the statement context, the analyzed context, the caret
        /// position and the popup contents to the log.  Kept in memory like <see cref="Enabled"/> so
        /// the disabled check costs one volatile read per keystroke.
        /// </summary>
        public static bool Detailed
        {
            get => Volatile.Read(ref detailed) == 1;
            set
            {
                int next = value ? 1 : 0;
                int previous = Interlocked.Exchange(ref detailed, next);
                if (previous == next) return;
                if (value) Write("SESSION", "detailed autocomplete log enabled: context, structured context, caret and popup contents");
                else Write("SESSION", "detailed autocomplete log disabled");
            }
        }

        /// <summary>The detailed completion log only writes while test mode is on as well.</summary>
        public static bool DetailedEnabled => Enabled && Detailed;

        /// <summary>Elapsed time since the extension loaded, used to correlate a user action with the
        /// keystrokes the completion pipeline saw.</summary>
        public static long ElapsedMilliseconds => SessionClock.ElapsedMilliseconds;

        public static void Write(string category, string message) => Record(RuntimeLogLevel.Info, category, message, null);

        /// <summary>Records a detailed autocomplete line only when both the test-mode and the
        /// detailed-autocomplete switches are on.  Call sites must keep the text building behind
        /// <see cref="DetailedEnabled"/> so the off path allocates nothing.</summary>
        public static void Detail(string category, string message)
            => Record(RuntimeLogLevel.Info, category, message, null, requireDetailed: true);

        public static void Warn(string category, string message) => Record(RuntimeLogLevel.Warn, category, message, null);

        public static void Error(string category, string message, Exception exception = null)
            => Record(RuntimeLogLevel.Error, category, message, exception);

        /// <summary>Records a line only when test mode is on; the call site keeps the message building
        /// behind this check so a disabled log costs nothing.</summary>
        public static void Perf(string category, string message, long elapsedMilliseconds)
            => Record(RuntimeLogLevel.Info, category, message + " (" + elapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms)", null);

        private static void Record(RuntimeLogLevel level, string category, string message, Exception exception,
            bool requireDetailed = false)
        {
            if (!Enabled) return;
            if (requireDetailed && !Detailed) return;
            var entry = new RuntimeLogEntry
            {
                Time = DateTime.Now,
                Level = level,
                Category = category,
                Message = LogRedaction.Redact(exception == null
                    ? message
                    : message + ": " + exception.GetType().Name + ": " + exception.Message),
                ThreadId = Thread.CurrentThread.ManagedThreadId
            };
            lock (Sync)
            {
                Entries.Add(entry);
                if (Entries.Count > Capacity) Entries.RemoveRange(0, Entries.Count - Capacity);
            }

            // The file log keeps the same lines, so a diagnostics-page report and the log file agree.
            try
            {
                string line = entry.Format();
                if (level == RuntimeLogLevel.Info) MSSQLToolPackage._logger?.Info("TEST " + line);
                else MSSQLToolPackage._logger?.Warn("TEST " + line);
            }
            catch (Exception) { }

            try { Logged?.Invoke(entry); }
            catch (Exception) { }
        }

        public static RuntimeLogEntry[] Snapshot()
        {
            lock (Sync) return Entries.ToArray();
        }

        public static void Clear()
        {
            lock (Sync) Entries.Clear();
        }

        public static string FormatAll()
        {
            var text = new StringBuilder();
            foreach (RuntimeLogEntry entry in Snapshot()) text.AppendLine(entry.Format());
            return text.ToString();
        }

        private static string DescribeEnvironment()
        {
            string assembly;
            try { assembly = typeof(RuntimeLog).Assembly.GetName().Version?.ToString() ?? "?"; }
            catch (Exception) { assembly = "?"; }
            return "MSSQL Tool " + assembly
                + " | " + Environment.OSVersion.VersionString
                + " | " + (Environment.Is64BitProcess ? "64-bit" : "32-bit")
                + " | pid " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
        }
    }
}
