using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MSSQLTool.Completion
{
    /// <summary>
    /// State of one cache entry.  <see cref="Loading"/> and <see cref="Refreshing"/> differ in what a
    /// reader gets: a loading entry has no snapshot yet, a refreshing entry already serves the previous
    /// snapshot while the replacement loads (stale-while-revalidate).
    /// </summary>
    internal enum MetadataEntryState
    {
        Empty,
        Loading,
        Ready,
        Refreshing,
        Failed
    }

    /// <summary>
    /// Cheap fingerprint of the catalog.  It is compared instead of reloading a whole database every
    /// 30 minutes: four aggregate queries (a few milliseconds) tell whether object names, columns,
    /// indexes or check constraints changed.
    /// <para>
    /// Known blind spots: operations that neither change <c>sys.all_objects.modify_date</c> nor a
    /// count (for example rebuilding an index with different key order) are not detected.  A manual
    /// refresh and the periodic full reload cover those.
    /// </para>
    /// </summary>
    internal struct MetadataSignature : IEquatable<MetadataSignature>
    {
        public readonly long Objects;
        public readonly long ObjectIdSum;
        public readonly long Columns;
        public readonly long Indexes;
        public readonly long Constraints;
        public readonly DateTime MaxModifyUtc;

        public MetadataSignature(long objects, long objectIdSum, DateTime maxModifyUtc, long columns, long indexes, long constraints)
        {
            Objects = objects;
            ObjectIdSum = objectIdSum;
            MaxModifyUtc = maxModifyUtc;
            Columns = columns;
            Indexes = indexes;
            Constraints = constraints;
        }

        public bool HasValue => Objects > 0 || Columns > 0 || Indexes > 0 || Constraints > 0;

        public bool Equals(MetadataSignature other)
            => Objects == other.Objects
                && ObjectIdSum == other.ObjectIdSum
                && MaxModifyUtc == other.MaxModifyUtc
                && Columns == other.Columns
                && Indexes == other.Indexes
                && Constraints == other.Constraints;

        public override bool Equals(object obj) => obj is MetadataSignature other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Objects.GetHashCode();
                hash = (hash * 397) ^ ObjectIdSum.GetHashCode();
                hash = (hash * 397) ^ MaxModifyUtc.GetHashCode();
                hash = (hash * 397) ^ Columns.GetHashCode();
                hash = (hash * 397) ^ Indexes.GetHashCode();
                hash = (hash * 397) ^ Constraints.GetHashCode();
                return hash;
            }
        }

        public override string ToString()
            => Objects.ToString(CultureInfo.InvariantCulture) + " obj/" + ObjectIdSum.ToString(CultureInfo.InvariantCulture)
                + "/" + MaxModifyUtc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
                + " " + Columns.ToString(CultureInfo.InvariantCulture) + " col/"
                + Indexes.ToString(CultureInfo.InvariantCulture) + " idx/"
                + Constraints.ToString(CultureInfo.InvariantCulture) + " ck";
    }

    /// <summary>Raised after a snapshot became readable, so open popups can re-render their candidates.</summary>
    internal sealed class MetadataSnapshotChange
    {
        public string Key { get; set; }
        public string Scope { get; set; }
        public string Reason { get; set; }
        public MetadataSnapshot Snapshot { get; set; }
    }

    /// <summary>One row of the cache status shown in Settings → SQL Completion.</summary>
    internal sealed class MetadataCacheDiagnostic
    {
        public string Key { get; set; }
        public string Scope { get; set; }
        public string State { get; set; }
        public string Source { get; set; }
        public string Error { get; set; }
        public int Objects { get; set; }
        public int Columns { get; set; }
        public int Parameters { get; set; }
        public DateTime LoadedUtc { get; set; }
        public DateTime LastAccessUtc { get; set; }
        public DateTime? LastValidatedUtc { get; set; }
        public long Hits { get; set; }
        public long Misses { get; set; }
        public long Refreshes { get; set; }
        public long DiskSeeds { get; set; }
        public double LastLoadMilliseconds { get; set; }
        public long EstimatedBytes { get; set; }
    }

    internal static class MetadataDiagnosticsFormatter
    {
        /// <summary>
        /// One block of text for the settings page.  Deliberately plain text: it is copied into bug
        /// reports, so every number needed to explain a slow popup has to be on one line.
        /// </summary>
        public static string Format(IEnumerable<MetadataCacheDiagnostic> entries, DateTime nowUtc)
        {
            var list = (entries ?? Enumerable.Empty<MetadataCacheDiagnostic>()).ToList();
            if (list.Count == 0) return LocalizationManager.T("No completion metadata is cached.");

            var text = new StringBuilder();
            long totalBytes = 0;
            foreach (MetadataCacheDiagnostic entry in list.OrderByDescending(e => e.LastAccessUtc))
            {
                totalBytes += entry.EstimatedBytes;
                text.AppendLine(Describe(entry, nowUtc));
            }
            text.Append(LocalizationManager.Format("Estimated memory: {0:0.0} MB across {1} database(s).",
                totalBytes / (1024.0 * 1024.0), list.Count));
            return text.ToString();
        }

        public static string Describe(MetadataCacheDiagnostic entry, DateTime nowUtc)
        {
            if (entry == null) return string.Empty;

            double idleMinutes = Math.Max(0, (nowUtc - entry.LastAccessUtc).TotalMinutes);
            string loaded = entry.LoadedUtc == DateTime.MinValue
                ? "-"
                : entry.LoadedUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            string validated = entry.LastValidatedUtc.HasValue
                ? entry.LastValidatedUtc.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture)
                : "-";

            var text = new StringBuilder();
            // The cache status is copied into support reports, so the same masking as the logs applies.
            // Scope is "server/database"; a cache key is a connection hash and carries no name.
            text.Append(!string.IsNullOrEmpty(entry.Scope)
                ? LogRedaction.RedactScope(entry.Scope)
                : LogRedaction.Redact(entry.Key ?? "?"));
            text.Append("  [").Append(entry.State ?? "?").Append("]");
            if (!string.IsNullOrWhiteSpace(entry.Source)) text.Append(" src=").Append(LogRedaction.Redact(entry.Source));
            text.AppendLine();
            text.Append("    ").Append(LocalizationManager.Format("{0} objects / {1} columns / {2} parameters",
                entry.Objects, entry.Columns, entry.Parameters));
            text.AppendLine();
            text.Append("    loaded ").Append(loaded)
                .Append(" · validated ").Append(validated)
                .Append(" · idle ").Append(idleMinutes.ToString("0.0", CultureInfo.CurrentCulture)).Append(" min")
                .Append(" · load ").Append(entry.LastLoadMilliseconds.ToString("0", CultureInfo.CurrentCulture)).Append(" ms");
            text.AppendLine();
            text.Append("    hits ").Append(entry.Hits)
                .Append(" · misses ").Append(entry.Misses)
                .Append(" · refreshes ").Append(entry.Refreshes)
                .Append(" · disk seeds ").Append(entry.DiskSeeds)
                .Append(" · ~").Append((entry.EstimatedBytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.CurrentCulture)).Append(" MB");
            if (!string.IsNullOrWhiteSpace(entry.Error)) text.Append(" · ").Append(LogRedaction.Redact(entry.Error));
            return text.ToString();
        }

        /// <summary>Rough working-set estimate: object shell + columns + parameters.  Not a measurement.</summary>
        public static long EstimateBytes(int objects, int columns, int parameters)
            => (objects * 430L) + (columns * 210L) + (parameters * 90L);
    }
}
