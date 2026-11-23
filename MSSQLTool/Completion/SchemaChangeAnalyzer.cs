using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MSSQLTool.Completion
{
    /// <summary>One object a DDL statement touched.  <see cref="Schema"/> is null when the script
    /// only wrote an unqualified name and the schema is therefore unknown.</summary>
    internal sealed class SchemaObjectReference
    {
        public string Schema { get; set; }
        public string Name { get; set; }

        internal string Key => ((Schema ?? string.Empty) + "|" + (Name ?? string.Empty)).ToLowerInvariant();

        public override string ToString() => string.IsNullOrEmpty(Schema) ? Name : Schema + "." + Name;
    }

    internal sealed class SchemaChangeSet
    {
        public List<SchemaObjectReference> Objects { get; } = new List<SchemaObjectReference>();
        /// <summary>True when the change cannot be expressed as "refresh these objects" (renames,
        /// schema moves, dynamic SQL, cross-database DDL) and the whole snapshot has to reload.</summary>
        public bool RequiresFullReload { get; set; }

        public bool HasChanges => RequiresFullReload || Objects.Count > 0;

        /// <summary>Temp tables (#t) are session-local and never live in the catalog; DDL on them must
        /// not make the analyzer report a change.</summary>
        internal int IgnoredTempTables { get; set; }

        /// <summary>Adds one target. Returns false when the name was a temp table and was ignored.</summary>
        public bool Add(string schema, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            string normalizedName = Unquote(name);
            if (string.IsNullOrEmpty(normalizedName)) return true;
            if (normalizedName.StartsWith("#", StringComparison.Ordinal))
            {
                IgnoredTempTables++;
                // Not a failure: the caller must not turn a temp-table batch into a full reload.
                return true;
            }
            string normalizedSchema = string.IsNullOrWhiteSpace(schema) ? null : Unquote(schema);
            foreach (SchemaObjectReference existing in Objects)
                if (string.Equals(existing.Name, normalizedName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Schema ?? string.Empty, normalizedSchema ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                    return true;
            Objects.Add(new SchemaObjectReference { Schema = normalizedSchema, Name = normalizedName });
            return true;
        }

        internal static string Unquote(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            string trimmed = value.Trim();
            if (trimmed.Length >= 2)
            {
                char first = trimmed[0];
                char last = trimmed[trimmed.Length - 1];
                if ((first == '[' && last == ']') || (first == '"' && last == '"'))
                    return trimmed.Substring(1, trimmed.Length - 2).Replace("]]", "]");
            }
            return trimmed;
        }
    }

    /// <summary>
    /// Turns an executed batch into the set of catalog objects it changed.  This replaces the old
    /// keyword sniffing (<c>CREATE|ALTER|DROP...</c>): the popup used to throw its whole snapshot away
    /// and reload the database synchronously, even when a single table gained one column.
    /// <para>
    /// Deliberately conservative: anything it cannot attribute to concrete objects raises
    /// <see cref="SchemaChangeSet.RequiresFullReload"/> instead of silently keeping stale columns.
    /// </para>
    /// </summary>
    internal static class SchemaChangeAnalyzer
    {
        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // name | schema.name | database.schema.name, each part bare, [bracketed] or "quoted"
        private const string Part = @"(?:\[[^\]]+\]|""[^""]+""|[A-Za-z_@#$][\w@#$]*)";
        private const string QualifiedName = Part + @"(?:\s*\.\s*" + Part + @"){0,2}";

        private static readonly Regex CreateOrAlterObject = new Regex(
            @"\b(?:CREATE\s+(?:OR\s+ALTER\s+)?|ALTER\s+)(?:TABLE|VIEW|PROC(?:EDURE)?|FUNCTION|SYNONYM|TYPE|SEQUENCE|TRIGGER)\s+(?:IF\s+(?:NOT\s+)?EXISTS\s+)?(?<name>" + QualifiedName + @")",
            Options);

        private static readonly Regex DropObject = new Regex(
            @"\bDROP\s+(?:TABLE|VIEW|PROC(?:EDURE)?|FUNCTION|SYNONYM|TYPE|SEQUENCE|TRIGGER)\s+(?:IF\s+EXISTS\s+)?(?<list>[^;]+)",
            Options);

        private static readonly Regex TruncateTable = new Regex(
            @"\bTRUNCATE\s+TABLE\s+(?<name>" + QualifiedName + @")", Options);

        private static readonly Regex CreateIndex = new Regex(
            @"\bCREATE\s+(?:UNIQUE\s+)?(?:(?:CLUSTERED|NONCLUSTERED)\s+)?INDEX\s+(?:\[[^\]]+\]|[A-Za-z_@#$][\w@#$]*)\s+ON\s+(?<name>" + QualifiedName + @")",
            Options);

        private static readonly Regex AlterIndex = new Regex(
            @"\bALTER\s+INDEX\s+(?:\[[^\]]+\]|[A-Za-z_@#$][\w@#$]*)\s+ON\s+(?<name>" + QualifiedName + @")",
            Options);

        private static readonly Regex DropIndex = new Regex(
            @"\bDROP\s+INDEX\s+(?:\[[^\]]+\]|[A-Za-z_@#$][\w@#$]*)\s+ON\s+(?<name>" + QualifiedName + @")",
            Options);

        private static readonly Regex AddColumn = new Regex(
            @"\bALTER\s+TABLE\s+(?<name>" + QualifiedName + @")", Options);

        private static readonly Regex SchemaOperation = new Regex(
            @"\b(?:CREATE|ALTER|DROP)\s+SCHEMA\b", Options);

        private static readonly Regex Rename = new Regex(@"\bsp_rename\b", Options);

        private static readonly Regex DatabaseOperation = new Regex(
            @"\b(?:CREATE|ALTER|DROP)\s+DATABASE\b", Options);

        private static readonly Regex StructuralKeyword = new Regex(
            @"\b(?:CREATE|ALTER|DROP|TRUNCATE|RENAME)\b", Options);

        private static readonly Regex DynamicExecute = new Regex(
            @"\bEXEC(?:UTE)?\s*(?:\(|\@)", Options);

        public static SchemaChangeSet Analyze(string sql)
        {
            var result = new SchemaChangeSet();
            if (string.IsNullOrWhiteSpace(sql)) return result;

            // Comments and string literals must never contribute a target: a commented-out
            // "DROP TABLE" used to throw the cache away.
            string masked = SqlTextContext.MaskCommentsAndStrings(sql);
            if (string.IsNullOrWhiteSpace(masked)) return result;

            // sp_rename and database-level DDL contain no CREATE/ALTER/DROP token of their own, so
            // they have to open the gate before the keyword check rejects them.
            bool rename = Rename.IsMatch(masked);
            bool databaseOperation = DatabaseOperation.IsMatch(masked);
            if (!StructuralKeyword.IsMatch(masked) && !rename && !databaseOperation) return result;

            if (rename || SchemaOperation.IsMatch(masked) || databaseOperation)
                result.RequiresFullReload = true;

            // EXEC(@sql) / EXEC sp_executesql @sql may build DDL at runtime; attribute nothing and reload.
            if (DynamicExecute.IsMatch(masked)) result.RequiresFullReload = true;

            foreach (Match match in CreateOrAlterObject.Matches(masked))
                AddQualified(result, match.Groups["name"].Value);

            foreach (Match match in AddColumn.Matches(masked))
                AddQualified(result, match.Groups["name"].Value);

            foreach (Match match in CreateIndex.Matches(masked))
                AddQualified(result, match.Groups["name"].Value);

            foreach (Match match in AlterIndex.Matches(masked))
                AddQualified(result, match.Groups["name"].Value);

            foreach (Match match in DropIndex.Matches(masked))
                AddQualified(result, match.Groups["name"].Value);

            foreach (Match match in TruncateTable.Matches(masked))
                AddQualified(result, match.Groups["name"].Value);

            foreach (Match match in DropObject.Matches(masked))
            {
                // DROP TABLE a, b, c — every element is a target.  A parenthesised tail means the
                // statement was not what it looked like, so fall back to a full reload.
                string list = match.Groups["list"].Value;
                if (list.IndexOf('(') >= 0) { result.RequiresFullReload = true; continue; }
                foreach (string element in list.Split(','))
                {
                    string candidate = element.Trim();
                    if (candidate.Length == 0) continue;
                    int space = candidate.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
                    if (space > 0) candidate = candidate.Substring(0, space);
                    if (!AddQualified(result, candidate)) result.RequiresFullReload = true;
                }
            }

            // A structural statement we produced no target for must not be treated as "no change".
            // Temp-table-only batches are the exception: they never touch the catalog.
            if (!result.RequiresFullReload && result.Objects.Count == 0 && result.IgnoredTempTables == 0)
                result.RequiresFullReload = true;
            return result;
        }

        private static bool AddQualified(SchemaChangeSet set, string qualifiedName)
        {
            if (string.IsNullOrWhiteSpace(qualifiedName)) return false;
            string[] parts = SplitParts(qualifiedName);
            for (int i = 0; i < parts.Length; i++) parts[i] = SchemaChangeSet.Unquote(parts[i].Trim());

            if (parts.Length == 0) return false;

            if (parts.Length == 1)
            {
                // Unqualified: the schema is unknown. The refresh resolves it by name.
                return set.Add(null, parts[0]);
            }

            if (parts.Length == 2)
            {
                return set.Add(parts[0], parts[1]);
            }

            // database.schema.object: this cache is keyed per database, so a change in another
            // database cannot be patched here. Reload instead of pretending to be up to date.
            set.RequiresFullReload = true;
            return set.Add(parts[parts.Length - 2], parts[parts.Length - 1]);
        }

        /// <summary>Splits on dots outside [brackets] and "quotes", so [dbo].[my.table] stays one part.</summary>
        private static string[] SplitParts(string qualifiedName)
        {
            var parts = new List<string>();
            var current = new System.Text.StringBuilder();
            char quote = '\0';
            foreach (char value in qualifiedName)
            {
                if (quote != '\0')
                {
                    current.Append(value);
                    if (value == quote) quote = '\0';
                    continue;
                }
                if (value == '[' || value == '"') { quote = value == '[' ? ']' : value; current.Append(value); continue; }
                if (value == '.') { parts.Add(current.ToString()); current.Length = 0; continue; }
                current.Append(value);
            }
            if (current.Length > 0) parts.Add(current.ToString());
            return parts.ToArray();
        }

        /// <summary>True when the batch may have changed the catalog. Kept for callers that only
        /// need the yes/no answer.</summary>
        public static bool IsStructuralChange(string sql) => Analyze(sql).HasChanges;
    }
}
