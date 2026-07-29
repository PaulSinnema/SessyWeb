namespace SessyController.Services
{
    /// <summary>
    /// Splits an SQL script into separate statements so they can be executed one after the
    /// other on a single connection. Used by the SQL Console on the Settings page.
    ///
    /// Semicolons inside string literals, quoted identifiers and comments do not end a
    /// statement. Comments are stripped.
    ///
    /// Two kinds of line are dropped rather than executed, because both would otherwise
    /// break a script that runs fine in the sqlite3 shell:
    ///   * dot-commands (.headers, .mode) — shell commands, not SQL. SQLite answers them
    ///     with: near ".": syntax error
    ///   * BEGIN/COMMIT/ROLLBACK — the caller wraps the whole script in one transaction,
    ///     and SQLite refuses a nested one.
    /// Both are reported through <paramref name="skipped"/>.
    /// </summary>
    public static class SqlScriptSplitter
    {
        public static List<string> Split(string script, out List<string> skipped)
        {
            var statements = new List<string>();
            skipped = new List<string>();

            if (string.IsNullOrWhiteSpace(script))
                return statements;

            var current = new System.Text.StringBuilder();

            char quote = '\0';       // active string/identifier delimiter, '\0' when outside one
            bool lineComment = false;
            bool blockComment = false;
            bool dotCommand = false;

            for (int i = 0; i < script.Length; i++)
            {
                char c = script[i];
                char next = i + 1 < script.Length ? script[i + 1] : '\0';

                if (dotCommand)
                {
                    if (c == '\n') dotCommand = false;
                    continue;
                }

                if (lineComment)
                {
                    if (c == '\n') { lineComment = false; current.Append(c); }
                    continue;
                }

                if (blockComment)
                {
                    if (c == '*' && next == '/') { blockComment = false; current.Append(' '); i++; }
                    continue;
                }

                if (quote != '\0')
                {
                    current.Append(c);
                    if (c == quote)
                    {
                        // '' inside a string is an escaped quote, not the end of it.
                        if (c == '\'' && next == '\'') { current.Append(next); i++; }
                        else quote = '\0';
                    }
                    continue;
                }

                if (c == '-' && next == '-') { lineComment = true; i++; continue; }
                if (c == '/' && next == '*') { blockComment = true; i++; continue; }

                // A dot only starts a shell command at the start of a statement; anywhere else
                // it is a decimal point or a schema qualifier such as temp.bounds.
                if (c == '.' && IsBlank(current))
                {
                    int end = script.IndexOf('\n', i);
                    string command = (end < 0 ? script[i..] : script[i..end]).Trim();
                    if (command.Length > 0) skipped.Add(command);
                    dotCommand = true;
                    continue;
                }

                if (c == '\'' || c == '"' || c == '`') { quote = c; current.Append(c); continue; }
                if (c == '[') { quote = ']'; current.Append(c); continue; }

                if (c == ';')
                {
                    Add(statements, skipped, current);
                    continue;
                }

                current.Append(c);
            }

            // A trailing statement without a closing semicolon still counts.
            Add(statements, skipped, current);

            return statements;
        }

        private static bool IsBlank(System.Text.StringBuilder sb)
        {
            for (int i = 0; i < sb.Length; i++)
                if (!char.IsWhiteSpace(sb[i])) return false;

            return true;
        }

        private static void Add(List<string> statements, List<string> skipped, System.Text.StringBuilder current)
        {
            var statement = current.ToString().Trim();
            current.Clear();

            if (statement.Length == 0)
                return;

            var head = statement.ToUpperInvariant();

            if (IsWord(head, "BEGIN") || IsWord(head, "COMMIT") ||
                IsWord(head, "ROLLBACK") || IsWord(head, "END"))
            {
                skipped.Add(statement.Split('\n')[0].Trim());
                return;
            }

            // A trigger body carries its own semicolons inside BEGIN ... END, which this
            // splitter would cut apart. Refuse rather than execute the fragments.
            if (head.StartsWith("CREATE TRIGGER") ||
                head.StartsWith("CREATE TEMP TRIGGER") ||
                head.StartsWith("CREATE TEMPORARY TRIGGER"))
            {
                throw new InvalidOperationException(
                    "CREATE TRIGGER is not supported in the console — its BEGIN ... END body cannot be split reliably. Use an EF migration instead.");
            }

            statements.Add(statement);
        }

        /// <summary>True when the statement starts with this keyword as a whole word.</summary>
        private static bool IsWord(string statement, string keyword)
        {
            if (!statement.StartsWith(keyword)) return false;

            return statement.Length == keyword.Length ||
                   !char.IsLetterOrDigit(statement[keyword.Length]) && statement[keyword.Length] != '_';
        }
    }
}
