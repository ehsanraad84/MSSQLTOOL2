using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MSSQLTool.Completion
{
    /// <summary>
    /// Diagnostic channel for the completion popup, written to the extension log at Information
    /// level.  The popup deals with Win32 focus, the shell's z-order and the editor caret at the same
    /// time; when a click closes it, none of that can be reconstructed from a stack trace, so the
    /// decisions are logged with the state they were made from.
    /// </summary>
    internal static class CompletionTrace
    {
        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        public static void Write(string message)
        {
            if (!RuntimeLog.Enabled) return;
            RuntimeLog.Write("COMPLETE", Clock.ElapsedMilliseconds + "ms " + message);
        }

        /// <summary>
        /// True only while test mode and the detailed autocomplete switch are both on.  The call
        /// sites use it to skip building the detail text entirely when the switch is off; a
        /// keystroke must not pay for a dump nobody asked for.
        /// </summary>
        public static bool DetailEnabled => RuntimeLog.DetailedEnabled;

        /// <summary>
        /// Writes one detailed autocomplete line.  Used for the four things the diagnostics page
        /// promises when "Detailed autocomplete" is checked: the statement context, the analyzed
        /// completion context (the structured data), the caret position and the popup contents.
        /// </summary>
        public static void Detail(string message) => RuntimeLog.Detail("COMPLETE", message);

        /// <summary>
        /// The analyzed context as one key=value line: what the completion pipeline decided the
        /// caret position means, which prefix and qualifier it read, and which aliases, locals,
        /// variables and diagnostics came with it.  Plain text on purpose — it is read next to the
        /// popup screenshot it explains.
        /// </summary>
        public static string Describe(CompletionContext context)
        {
            if (context == null) return "context=null";

            var text = new StringBuilder(640);
            text.Append("kind=").Append(context.Kind)
                .Append(" resolved=").Append(context.ResolvedKind)
                .Append(" byTree=").Append(context.PositionDecidedByTree)
                .Append(" positionResolved=").Append(context.PositionResolved)
                .Append(" clause=").Append(Quote(context.EnclosingClause))
                .Append(" statementKind=").Append(Quote(context.StatementKind))
                .Append(" prefix=").Append(Quote(context.Prefix))
                .Append(" qualifier=").Append(Quote(context.Qualifier))
                .Append(" target=").Append(Quote(context.TargetObject))
                .Append(" source=").Append(Quote(context.TableSourceName))
                .Append(" aliasWritten=").Append(context.TableAliasWritten)
                .Append(" awaitingJoin=").Append(context.AwaitingJoinPredicate)
                .Append(" omittedSchema=").Append(context.HasOmittedSchemaQualifier)
                .Append(" predicate=").Append(context.PredicateState).Append('/').Append(context.PredicateDepth)
                .Append(" argumentIndex=").Append(context.ArgumentIndex)
                .Append(" explicit=").Append(context.IsExplicitRequest)
                .Append(" suppress=").Append(context.Suppress)
                .Append(" parseErrors=").Append(context.HasParseErrors)
                .Append(" scope=").Append(Quote(ScopeText(context.MetadataScope)));

            text.Append(" aliases=[");
            int index = 0;
            foreach (KeyValuePair<string, string> alias in context.Aliases ?? new Dictionary<string, string>())
            {
                if (index++ > 0) text.Append(", ");
                text.Append(alias.Key).Append('=').Append(alias.Value);
            }
            text.Append(']');

            text.Append(" locals=[");
            index = 0;
            foreach (DatabaseObjectMetadata local in context.LocalObjects ?? new List<DatabaseObjectMetadata>())
            {
                if (index++ > 0) text.Append(", ");
                text.Append(local.Kind).Append(':').Append(local.Name)
                    .Append('(').Append(local.Columns.Count).Append(" cols)");
            }
            text.Append(']');

            text.Append(" variables=[");
            index = 0;
            foreach (DeclaredVariableInfo variable in context.DeclaredVariables ?? new List<DeclaredVariableInfo>())
            {
                if (index++ > 0) text.Append(", ");
                text.Append(variable.Name).Append(':').Append(variable.Type);
            }
            text.Append(']');

            text.Append(" diagnostics=[");
            index = 0;
            foreach (SqlEditorDiagnostic diagnostic in context.Diagnostics ?? new List<SqlEditorDiagnostic>())
            {
                if (index++ > 0) text.Append(", ");
                text.Append(diagnostic.Code).Append(':').Append(Snippet(diagnostic.Message, 120));
            }
            text.Append(']');

            text.Append(" statementText=").Append(Quote(Snippet(context.CurrentStatement, 400)));
            return text.ToString();
        }

        /// <summary>
        /// The rows the popup shows, in display order, so a report can be checked against what the
        /// user saw.  Bounded: candidate lists can hold thousands of rows.
        /// </summary>
        public static string DescribeItems(IReadOnlyList<CompletionItem> items, int maximum = 40)
        {
            if (items == null || items.Count == 0) return "items=0[]";

            int shown = Math.Max(0, Math.Min(items.Count, maximum));
            var text = new StringBuilder(64 + (shown * 32));
            text.Append("items=").Append(items.Count).Append('[');
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) text.Append(", ");
                CompletionItem item = items[i];
                if (item == null) { text.Append("null"); continue; }
                text.Append(item.Kind).Append(':').Append(item.DisplayText);
                if (item.Score != 0) text.Append('#').Append(item.Score.ToString(CultureInfo.InvariantCulture));
                if (item.IsSystem) text.Append('*');
            }
            if (items.Count > shown) text.Append(", +").Append(items.Count - shown).Append(" more");
            return text.Append(']').ToString();
        }

        /// <summary>One log line, so an excerpt never breaks the entry it belongs to.</summary>
        public static string Snippet(string text, int maximum)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            string singleLine = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            if (maximum <= 0 || singleLine.Length <= maximum) return singleLine;
            return singleLine.Substring(0, maximum) + "…";
        }

        public static string Quote(string value) => "'" + (value ?? string.Empty) + "'";

        /// <summary>The cache scope is "server|database"; log it as "server/database" so the log
        /// redaction can keep both names as separate tokens.</summary>
        private static string ScopeText(string scope)
            => string.IsNullOrEmpty(scope) ? string.Empty : scope.Replace('|', '/');

        public static void Failure(string message, Exception exception)
        {
            if (RuntimeLog.Enabled)
            {
                RuntimeLog.Warn("COMPLETE", Clock.ElapsedMilliseconds + "ms " + message
                    + (exception == null ? string.Empty : ": " + exception.Message));
                return;
            }
            // Failures are worth keeping outside test mode; the informational decisions are not.
            try { MSSQLToolPackage._logger?.Warn("COMPLETE " + Clock.ElapsedMilliseconds + "ms " + message + ": " + exception?.Message); }
            catch (Exception) { }
        }

        /// <summary>Focus, foreground and the popup handle in one line, for gesture logs.</summary>
        public static string State(IntPtr popup)
        {
            try
            {
                IntPtr focus = GetFocus();
                IntPtr foreground = GetForegroundWindow();
                return "focus=" + Describe(focus) + " foreground=" + Describe(foreground) + " popup=" + Describe(popup)
                    + " popupHasFocus=" + (focus != IntPtr.Zero && focus == popup);
            }
            catch (Exception)
            {
                return "focus=unavailable";
            }
        }

        public static string Describe(IntPtr window)
        {
            if (window == IntPtr.Zero) return "0";
            try
            {
                var title = new StringBuilder(120);
                GetWindowText(window, title, title.Capacity);
                var cls = new StringBuilder(120);
                GetClassName(window, cls, cls.Capacity);
                return "0x" + window.ToInt64().ToString("X") + "(" + cls + ":'" + Shorten(title.ToString()) + "')";
            }
            catch (Exception)
            {
                return "0x" + window.ToInt64().ToString("X");
            }
        }

        private static string Shorten(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length <= 40 ? text : text.Substring(0, 40) + "…";
        }
    }
}
