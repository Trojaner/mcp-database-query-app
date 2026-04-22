using System.Text.RegularExpressions;

namespace McpDatabaseQueryApp.Core.Scripts;

public static partial class ScriptSafetyAnalyzer
{
    [GeneratedRegex(@"^\s*--[^\n]*", RegexOptions.Multiline)]
    private static partial Regex LineCommentRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockCommentRegex();

    [GeneratedRegex(@"\b(drop|truncate|alter\s+table\s+[\w\.""\[\]]+\s+drop|grant|revoke|shutdown|kill)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DestructiveKeywordRegex();

    [GeneratedRegex(@"\b(delete|update)\s+(?:from\s+)?[\w\.""\[\]]+(?:\s+set\s+[^;]*)?\s*(;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex UnqualifiedMutationRegex();

    [GeneratedRegex(@"\bwhere\b", RegexOptions.IgnoreCase)]
    private static partial Regex WhereClauseRegex();

    public static bool IsLikelyDestructive(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return false;
        }

        var stripped = BlockCommentRegex().Replace(sql, " ");
        stripped = LineCommentRegex().Replace(stripped, " ");

        if (DestructiveKeywordRegex().IsMatch(stripped))
        {
            return true;
        }

        foreach (Match match in UnqualifiedMutationRegex().Matches(stripped))
        {
            var fragment = match.Value;
            if (!WhereClauseRegex().IsMatch(fragment))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsReadOnly(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return true;
        }

        var stripped = BlockCommentRegex().Replace(sql, " ");
        stripped = LineCommentRegex().Replace(stripped, " ").TrimStart();
        return stripped.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || stripped.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
            || stripped.StartsWith("SHOW", StringComparison.OrdinalIgnoreCase)
            || stripped.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase);
    }
}
