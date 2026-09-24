using System.Text;
using System.Text.RegularExpressions;

public sealed record DatabaseQueryRequest(string Sql);

public sealed record DatabaseColumnRow(string TableSchema, string TableName, string ColumnName, string DataType);

/// <summary>A lightweight first-pass check that a submitted string is a single SELECT/WITH statement, rejecting
/// anything else outright before it ever reaches Postgres. This alone is not a real security boundary — a
/// cleverly-crafted statement could still slip past keyword matching — so the actual enforcement is the database
/// session itself refusing any write inside a <c>BEGIN TRANSACTION READ ONLY</c> block.</summary>
public static class SqlSelectValidator
{
    private static readonly string[] ForbiddenKeywords =
    [
        "INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "TRUNCATE", "GRANT", "CREATE", "CALL"
    ];

    public static bool IsReadOnlySelect(string? sql, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(sql)) { error = "SQL text is required."; return false; }

        var stripped = StripLiteralsAndComments(sql);
        var trimmed = stripped.Trim();
        if (trimmed.EndsWith(';')) trimmed = trimmed[..^1].TrimEnd();
        if (trimmed.Contains(';')) { error = "Only a single statement is allowed."; return false; }
        if (trimmed.Length == 0) { error = "SQL text is required."; return false; }

        var upper = trimmed.ToUpperInvariant();
        if (!upper.StartsWith("SELECT") && !upper.StartsWith("WITH"))
        {
            error = "Only SELECT (or WITH ... SELECT) statements are allowed.";
            return false;
        }

        foreach (var keyword in ForbiddenKeywords)
        {
            if (Regex.IsMatch(stripped, $@"\b{keyword}\b", RegexOptions.IgnoreCase))
            {
                error = $"Statement contains a disallowed keyword: {keyword}.";
                return false;
            }
        }

        return true;
    }

    // Blanks out string literals and comments so keyword matching never gets confused by "DELETE" appearing
    // inside a quoted value or a comment, and so a hidden second statement can't be smuggled inside a literal.
    private static string StripLiteralsAndComments(string sql)
    {
        var result = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '\'')
            {
                result.Append(' ');
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'') { i += 2; continue; }
                    if (sql[i] == '\'') { i++; break; }
                    i++;
                }
                continue;
            }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/')) i++;
                i = Math.Min(i + 2, sql.Length);
                continue;
            }
            result.Append(c);
            i++;
        }
        return result.ToString();
    }
}
