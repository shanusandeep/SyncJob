using System.Globalization;
using SyncExamSubJob.Configuration;

namespace SyncExamSubJob.Sql;

/// <summary>
/// Central place for the two pieces of raw SQL text construction that cannot use
/// parameters: escaping the inner Oracle query for OPENQUERY, and rendering the
/// Oracle date literal. Every identifier passed through here is already
/// whitelist-validated by <see cref="IdentifierValidator"/>; the only free value
/// is a <see cref="DateTime"/>, formatted with a fixed invariant pattern.
/// </summary>
public static class SqlText
{
    /// <summary>
    /// The inner Oracle SQL is embedded as a single-quoted string argument to
    /// OPENQUERY, so every single quote inside it must be doubled.
    /// </summary>
    public static string EscapeForOpenQuery(string innerOracleSql) =>
        innerOracleSql.Replace("'", "''");

    /// <summary>
    /// Render a SQL Server datetime as an Oracle date/timestamp literal. The
    /// form is the single point controlled by Sync:OracleDateLiteralMode so a
    /// DATE-vs-TIMESTAMP linked-server quirk is a config flip, not a code change.
    /// Uses a fixed invariant-culture pattern (never locale-dependent).
    /// </summary>
    public static string OracleDateLiteral(DateTime value, OracleDateMode mode)
    {
        if (mode == OracleDateMode.Date)
        {
            var s = value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            return $"TO_DATE('{s}','YYYY-MM-DD HH24:MI:SS')";
        }

        var t = value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        return $"TO_TIMESTAMP('{t}','YYYY-MM-DD HH24:MI:SS.FF3')";
    }
}
