using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MSSQLTool
{
    /// <summary>
    /// Removes connection details from everything the extension records for support: the runtime
    /// log on the diagnostics page, the feature diagnostics report and the log files.  Server
    /// addresses, instance names, database names, logins and secrets are replaced with stable
    /// per-session tokens (<c>&lt;server#1&gt;</c>, <c>&lt;database#2&gt;</c>) so two lines can still
    /// be correlated without naming the customer's environment.
    /// <para>
    /// The same names always get the same token, and the masking is idempotent: text that has
    /// already been redacted (including the "TEST" lines that are mirrored to the file log) passes
    /// through unchanged.
    /// </para>
    /// </summary>
    internal static class LogRedaction
    {
        private const string ServerKind = "server";
        private const string DatabaseKind = "database";
        private const string UserKind = "user";

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, int> Servers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> Databases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> Users = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // connection='[SRV01\PROD] \ [Sales]' — the display name used by the connection helper.
        private static readonly Regex Connection = new Regex(
            @"(?<key>\bconnection\s*=\s*')(?<value>[^'\r\n]*)'",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex BracketedConnection = new Regex(
            @"\[(?<server>[^\]\r\n]+)\]\s*\\\s*\[(?<database>[^\]\r\n]+)\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // scope='SRV01\PROD/Sales' — the metadata cache scope is "<server>/<database>".
        private static readonly Regex Scope = new Regex(
            @"(?<key>\bscope\s*=\s*')(?<value>[^'\r\n]*)'",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // databases='Sales,HR' linked='REPORTING' — lists of names in the completion trace.
        private static readonly Regex NameList = new Regex(
            @"(?<key>\b(?:databases|linked)\s*=\s*')(?<value>[^'\r\n]*)'",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Search failed for database Sales — the Quick Search diagnostics use this unquoted form.
        private static readonly Regex DatabasePhrase = new Regex(
            @"(?<key>\b(?:for|in|on|of|to|from)\s+database\s+)(?<value>[A-Za-z0-9_@#$.\-\\]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Data Source=SRV01\PROD;Initial Catalog=Sales;User ID=sa;Password=...
        // server='...' database='...' are the same shape and are handled here too.
        private static readonly Regex Assignments = new Regex(
            @"(?<key>\b(?:data\s+source|server|host|hostname|address|addr|network\s+address|initial\s+catalog|database|catalog|user\s+id|userid|uid|user|password|pwd)\s*=\s*)(?<value>'(?:''|[^'])*'|[^;'"",\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Cannot open database "Sales" ... Login failed for user 'CONTOSO\bob'.
        private static readonly Regex QuotedName = new Regex(
            @"(?<key>\b(?:linked\s+server|database|catalog|server|login|user|principal)\s*(?:name\s*)?[,:]?\s*=?\s*)(?<quote>['""])(?<value>(?:''|""""|[^'"")])*)\k<quote>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // 无法打开登录所请求的数据库 "Sales"。登录失败。 / 用户 'sa' 登录失败。
        // SqlClient localizes these messages, so the English-keyed patterns above never match them and
        // the customer's database and login names were written to the log verbatim.
        private static readonly Regex LocalizedDatabaseName = new Regex(
            @"(?<key>(?:数据库|資料庫)\s*)(?<quote>[""'])(?<value>[^""'\r\n]{1,128})\k<quote>",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex LocalizedLoginName = new Regex(
            @"(?<key>(?:用户|使用者|登录名|登入名|登录|登入)\s*)(?<quote>[""'])(?<value>[^""'\r\n]{1,128})\k<quote>",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex LocalizedServerName = new Regex(
            @"(?<key>(?:服务器|伺服器|实例|執行個體)\s*)(?<quote>[""'])(?<value>[^""'\r\n]{1,128})\k<quote>",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // token=..., api_key: ..., secret=...
        private static readonly Regex SecretAssignment = new Regex(
            @"(?<key>\b(?:password|pwd|secret|token|access[_-]?token|access[_-]?key|api[_-]?key|account[_-]?key|shared[_-]?access[_-]?signature|sas)\b\s*(?:=|:)\s*)(?<value>'(?:''|[^'])*'|[^\s,;]+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // A bare dotted number is usually a version (1.0.0.0); only an address followed by a
        // port is treated as a server address.  Data Source=/Server= values are handled above.
        private static readonly Regex Ipv4 = new Regex(
            @"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?=:\d{1,5}\b)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Masks connection details in one line of text.  Null and empty text is returned as is.</summary>
        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

            string result = Connection.Replace(text, RedactConnection);
            result = BracketedConnection.Replace(result, RedactBracketedConnection);
            result = Scope.Replace(result, RedactScopeMatch);
            result = NameList.Replace(result, RedactNameList);
            result = DatabasePhrase.Replace(result, RedactDatabasePhrase);
            result = Assignments.Replace(result, RedactAssignment);
            result = QuotedName.Replace(result, RedactQuotedName);
            result = LocalizedDatabaseName.Replace(result, RedactLocalizedDatabase);
            result = LocalizedLoginName.Replace(result, RedactLocalizedLogin);
            result = LocalizedServerName.Replace(result, RedactLocalizedServer);
            result = SecretAssignment.Replace(result, RedactSecret);
            result = Ipv4.Replace(result, "<server>");
            return RedactQualifiedDatabaseNames(result);
        }

        /// <summary>Same shape as <see cref="RedactQuotedName"/>, for the localized SQL Server
        /// messages that carry no English keyword.</summary>
        private static string RedactLocalizedDatabase(Match match) => RedactLocalized(match, DatabaseKind);

        private static string RedactLocalizedLogin(Match match) => RedactLocalized(match, UserKind);

        private static string RedactLocalizedServer(Match match) => RedactLocalized(match, ServerKind);

        private static string RedactLocalized(Match match, string kind)
        {
            string value = match.Groups["value"].Value;
            if (value.Length == 0 || LooksRedacted(value)) return match.Value;
            string quote = match.Groups["quote"].Value;
            return match.Groups["key"].Value + quote + Token(kind, value) + quote;
        }

        /// <summary>Masks a bare "server/database" scope (the metadata cache prints it without a key).</summary>
        public static string RedactScope(string scope)
        {
            if (string.IsNullOrEmpty(scope)) return scope ?? string.Empty;
            int slash = scope.LastIndexOf('/');
            if (slash <= 0 || slash == scope.Length - 1) return Token(ServerKind, scope);
            return Token(ServerKind, scope.Substring(0, slash)) + "/" + Token(DatabaseKind, scope.Substring(slash + 1));
        }

        /// <summary>Clears the token maps so a test can assert exact token numbers.</summary>
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                Servers.Clear();
                Databases.Clear();
                Users.Clear();
            }
        }

        private static string RedactConnection(Match match)
        {
            string value = match.Groups["value"].Value;
            if (value.Length == 0 || LooksRedacted(value)) return match.Value;
            return match.Groups["key"].Value + "<connection>'";
        }

        private static string RedactBracketedConnection(Match match)
        {
            string server = match.Groups["server"].Value;
            string database = match.Groups["database"].Value;
            return "[" + Token(ServerKind, server) + "] \\ [" + Token(DatabaseKind, database) + "]";
        }

        private static string RedactScopeMatch(Match match)
        {
            string value = match.Groups["value"].Value;
            if (value.Length == 0 || LooksRedacted(value)) return match.Value;
            return match.Groups["key"].Value + RedactScope(value) + "'";
        }

        private static string RedactNameList(Match match)
        {
            string value = match.Groups["value"].Value;
            if (value.Length == 0 || LooksRedacted(value)) return match.Value;

            string kind = match.Groups["key"].Value.IndexOf("linked", StringComparison.OrdinalIgnoreCase) >= 0
                ? ServerKind
                : DatabaseKind;
            string[] parts = value.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string name = parts[i].Trim();
                parts[i] = name.Length == 0 ? parts[i] : Token(kind, name);
            }
            return match.Groups["key"].Value + string.Join(",", parts) + "'";
        }

        private static string RedactDatabasePhrase(Match match)
        {
            string value = match.Groups["value"].Value;
            if (value.Length == 0 || LooksRedacted(value)) return match.Value;
            return match.Groups["key"].Value + Token(DatabaseKind, value);
        }

        private static string RedactAssignment(Match match)
        {
            string value = match.Groups["value"].Value;
            string quote = string.Empty;
            if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
            {
                quote = "'";
                value = value.Substring(1, value.Length - 2);
            }

            if (value.Length == 0 || LooksRedacted(value)) return match.Value;

            string key = match.Groups["key"].Value;
            string lower = key.ToLowerInvariant();
            string token;
            if (lower.Contains("password") || lower.Contains("pwd")) token = "<redacted>";
            else if (lower.Contains("user") || lower.Contains("uid")) token = Token(UserKind, value);
            else if (lower.Contains("catalog") || lower.Contains("database")) token = Token(DatabaseKind, value);
            else token = Token(ServerKind, value);
            return key + quote + token + quote;
        }

        private static string RedactQuotedName(Match match)
        {
            string value = match.Groups["value"].Value;
            if (value.Length == 0 || LooksRedacted(value)) return match.Value;

            string key = match.Groups["key"].Value;
            string lower = key.ToLowerInvariant();
            string kind;
            if (lower.Contains("user") || lower.Contains("login") || lower.Contains("principal")) kind = UserKind;
            else if (lower.Contains("database") || lower.Contains("catalog")) kind = DatabaseKind;
            else kind = ServerKind;

            string quote = match.Groups["quote"].Value;
            return key + quote + Token(kind, value) + quote;
        }

        /// <summary>
        /// A database name already seen in a structural field can appear again inside a qualified
        /// identifier (&quot;Sales.dbo.Orders&quot;, optionally bracketed).  Only names the log has
        /// already identified are replaced, and only when a dot follows, so ordinary prose and
        /// assembly names are left alone.
        /// </summary>
        private static string RedactQualifiedDatabaseNames(string text)
        {
            string[] names;
            lock (Gate)
            {
                if (Databases.Count == 0) return text;
                names = Databases.Keys.ToArray();
            }

            Array.Sort(names, (left, right) => right.Length.CompareTo(left.Length));
            foreach (string name in names)
            {
                if (name.Length < 3 || text.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string pattern = @"(?<![A-Za-z0-9_@#$])(?<open>\[)?" + Regex.Escape(name) + @"(?<close>\])?(?=\.)";
                string token = Token(DatabaseKind, name);
                text = Regex.Replace(text, pattern, "${open}" + token + "${close}",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            return text;
        }

        private static string RedactSecret(Match match)
        {
            string value = match.Groups["value"].Value;
            string quote = string.Empty;
            if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
            {
                quote = "'";
                value = value.Substring(1, value.Length - 2);
            }

            if (value.Length == 0 || LooksRedacted(value)) return match.Value;
            return match.Groups["key"].Value + quote + "<redacted>" + quote;
        }

        private static string Token(string kind, string value)
        {
            string name = (value ?? string.Empty).Trim();
            if (name.Length == 0 || LooksRedacted(name)) return value ?? string.Empty;

            Dictionary<string, int> map = kind == ServerKind ? Servers : kind == DatabaseKind ? Databases : Users;
            int number;
            lock (Gate)
            {
                if (!map.TryGetValue(name, out number))
                {
                    number = map.Count + 1;
                    map[name] = number;
                }
            }
            return "<" + kind + "#" + number.ToString(CultureInfo.InvariantCulture) + ">";
        }

        private static bool LooksRedacted(string value)
        {
            string text = (value ?? string.Empty).Trim();
            return text.Length >= 2 && text[0] == '<' && text[text.Length - 1] == '>';
        }
    }
}
