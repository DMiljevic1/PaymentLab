using System.Text.RegularExpressions;

namespace PaymentLab.ServiceDefaults;

/// <summary>Short, low-cardinality span names for SQL commands, e.g. "SELECT accounts" or "INSERT outbox_messages".</summary>
public static partial class SqlSpanName
{
    public static string From(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return "postgresql";
        var verb = Verb().Match(sql);
        if (!verb.Success) return "postgresql";
        var op = verb.Groups[1].Value.ToUpperInvariant();
        var table = op switch
        {
            "INSERT" => Into().Match(sql),
            "UPDATE" => UpdateTable().Match(sql),
            _ => FromTable().Match(sql),
        };
        return table.Success ? $"{op} {table.Groups[1].Value}" : op;
    }

    [GeneratedRegex(@"^\s*(SELECT|INSERT|UPDATE|DELETE|WITH|BEGIN|COMMIT|ROLLBACK|SAVEPOINT|RELEASE|CREATE|ALTER|DROP|LOCK|DISCARD|SET|SHOW)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Verb();

    [GeneratedRegex(@"\bINTO\s+""?([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase)]
    private static partial Regex Into();

    [GeneratedRegex(@"^\s*UPDATE\s+""?([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateTable();

    [GeneratedRegex(@"\bFROM\s+""?([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase)]
    private static partial Regex FromTable();
}
