using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MSSQLTool
{
    internal sealed class FeatureDiagnostic
    {
        public DateTime TimestampUtc { get; set; }
        public string Feature { get; set; }
        /// <summary>Message with server, database, login and secret values already masked.</summary>
        public string Message { get; set; }
        /// <summary>Exception type and message with the same masking applied; this is what the
        /// diagnostics page prints.</summary>
        public string Detail { get; set; }
    }

    internal static class FeatureDiagnostics
    {
        private const int Capacity = 100;
        private static readonly object SyncRoot = new object();
        private static readonly Queue<FeatureDiagnostic> Items = new Queue<FeatureDiagnostic>();

        public static void Report(string feature, string message, Exception exception = null)
        {
            var item = new FeatureDiagnostic
            {
                TimestampUtc = DateTime.UtcNow,
                Feature = feature ?? "General",
                Message = LogRedaction.Redact(message ?? string.Empty),
                Detail = exception == null
                    ? null
                    : LogRedaction.Redact(exception.GetType().Name + ": " + exception.Message)
            };
            lock (SyncRoot)
            {
                Items.Enqueue(item);
                while (Items.Count > Capacity) Items.Dequeue();
            }
            Trace.WriteLine($"MSSQL Tool [{item.Feature}] {item.Message}{(item.Detail == null ? string.Empty : ": " + item.Detail)}");
        }

        public static FeatureDiagnostic[] Snapshot()
        {
            lock (SyncRoot) return Items.ToArray();
        }
    }
}
