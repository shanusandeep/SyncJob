using System.Text;
using SyncExamSubJob.Configuration;
using SyncExamSubJob.Models;
using SyncExamSubJob.Services;

namespace SyncExamSubJob.Sql;

/// <summary>
/// Builds the per-table T-SQL. Every identifier here is whitelist-validated
/// (TableDefinition.Validate / IdentifierValidator) before this runs, so
/// bracket-quoting SQL Server names and inlining the (also validated) Oracle
/// names is safe. The only non-identifier values are the two Oracle date
/// literals, produced by <see cref="SqlText.OracleDateLiteral"/>.
/// </summary>
public static class SqlBuilder
{
    private static string B(string ident) => IdentifierValidator.Bracket(ident);

    /// <summary>
    /// Step B: create #STG with the EXACT target column types, then fill it from
    /// Oracle through OPENQUERY (predicate pushed down; bounded on both sides by
    /// the shared cut). Runs in autocommit (no explicit transaction) so the
    /// linked-server read never promotes to a distributed (MSDTC) transaction.
    /// </summary>
    public static string BuildStaging(
        TableDefinition table,
        IReadOnlyList<StagedColumn> stagedColumns,
        string linkedServer,
        string oracleSchema,
        string lowerBoundLiteral,
        string cutLiteral)
    {
        var sb = new StringBuilder();
        sb.AppendLine("SET NOCOUNT ON;");

        sb.AppendLine("IF OBJECT_ID('tempdb..#STG') IS NOT NULL DROP TABLE #STG;");
        sb.AppendLine("CREATE TABLE #STG (");
        for (var i = 0; i < stagedColumns.Count; i++)
        {
            var c = stagedColumns[i];
            sb.Append("    ").Append(B(c.Name)).Append(' ').Append(c.TypeDefinition).Append(" NULL");
            sb.AppendLine(i == stagedColumns.Count - 1 ? "" : ",");
        }
        sb.AppendLine(");");

        // Inner Oracle query (built with normal single quotes; escaped below).
        var oracleCols = string.Join(", ",
            table.StagingColumns.Select(c => $"{c.OracleExpression} AS {c.SqlName}"));

        var change = $"GREATEST(NVL({table.CreateDtmColumn},{table.ModifyDtmColumn})," +
                     $"NVL({table.ModifyDtmColumn},{table.CreateDtmColumn}))";

        var innerOracle =
            $"SELECT {oracleCols} FROM {oracleSchema}.{table.OracleTable} " +
            $"WHERE {change} > {lowerBoundLiteral} AND {change} <= {cutLiteral}";

        var escaped = SqlText.EscapeForOpenQuery(innerOracle);

        var sqlCols = string.Join(", ", table.StagingColumns.Select(c => B(c.SqlName)));

        sb.AppendLine($"INSERT INTO #STG ({sqlCols})");
        sb.AppendLine($"SELECT {sqlCols}");
        sb.AppendLine($"FROM OPENQUERY({B(linkedServer)}, '{escaped}') AS src;");

        return sb.ToString();
    }

    public static string BuildStagedRowCount() => "SELECT COUNT(*) FROM #STG;";

    /// <summary>
    /// Diagnostic query for one enabled FK on the target table. It returns a
    /// small sample of staged child rows whose non-null FK values do not have a
    /// matching parent row, so the log can identify the exact incoming data that
    /// would make MERGE fail.
    /// </summary>
    public static string BuildForeignKeyDiagnosticQuery(
        TableDefinition table,
        ForeignKeyInfo fk,
        int sampleRows)
    {
        var top = Math.Max(0, sampleRows).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var q = SqlText.BracketSqlServerIdentifier;
        var pk = q(table.PrimaryKey);
        var parentQualified = $"{q(fk.ParentSchema)}.{q(fk.ParentTable)}";

        var selectColumns = new List<string>
        {
            $"TRY_CONVERT(nvarchar(4000), s.{pk}) AS {q(table.PrimaryKey)}"
        };
        selectColumns.AddRange(fk.Columns
            .Where(c => !string.Equals(c.ChildColumn, table.PrimaryKey, StringComparison.OrdinalIgnoreCase))
            .Select(c => $"TRY_CONVERT(nvarchar(4000), s.{q(c.ChildColumn)}) AS {q(c.ChildColumn)}"));

        var join = string.Join(" AND ", fk.Columns.Select(c =>
            $"p.{q(c.ParentColumn)} = s.{q(c.ChildColumn)}"));

        // SQL Server does not enforce an FK when any child FK column is NULL.
        var childColumnsPresent = string.Join(" AND ", fk.Columns.Select(c =>
            $"s.{q(c.ChildColumn)} IS NOT NULL"));

        var parentMissingCheck = $"p.{q(fk.Columns[0].ParentColumn)} IS NULL";

        return
            "SELECT TOP (" + top + ") " + string.Join(", ", selectColumns) + Environment.NewLine +
            "FROM #STG AS s" + Environment.NewLine +
            "LEFT JOIN " + parentQualified + " AS p" + Environment.NewLine +
            "  ON " + join + Environment.NewLine +
            "WHERE " + childColumnsPresent + Environment.NewLine +
            "  AND " + parentMissingCheck + Environment.NewLine +
            $"ORDER BY s.{pk};";
    }

    /// <summary>
    /// Step C (real run): change-only upsert. WHEN MATCHED fires only if at least
    /// one synced column actually differs (null-safe), so the safety-overlap
    /// re-pull does not churn unchanged rows. IDENTITY_INSERT is toggled only
    /// when the target PK is an identity column.
    /// </summary>
    public static string BuildMergeBatch(TableDefinition table, string targetQualified, bool pkIsIdentity)
    {
        var pk = B(table.PrimaryKey);
        var diff = BuildDiffPredicate(table, "t", "s");

        var updateSet = string.Join(", ",
            table.NonKeyComparableColumns.Select(c => $"t.{B(c.SqlName)} = s.{B(c.SqlName)}"));

        var insertCols = string.Join(", ", table.Columns.Select(c => B(c.SqlName)));
        var insertVals = string.Join(", ",
            table.Columns.Select(c => c.IsLiteralNull ? "NULL" : $"s.{B(c.SqlName)}"));

        var sb = new StringBuilder();
        sb.AppendLine("SET NOCOUNT ON;");
        sb.AppendLine("DECLARE @act TABLE (act NVARCHAR(10));");
        if (pkIsIdentity)
            sb.AppendLine($"SET IDENTITY_INSERT {targetQualified} ON;");

        sb.AppendLine($"MERGE {targetQualified} WITH (HOLDLOCK) AS t");
        sb.AppendLine("USING #STG AS s");
        sb.AppendLine($"   ON t.{pk} = s.{pk}");
        sb.AppendLine($"WHEN MATCHED AND ({diff}) THEN");
        sb.AppendLine($"   UPDATE SET {updateSet}");
        sb.AppendLine("WHEN NOT MATCHED BY TARGET THEN");
        sb.AppendLine($"   INSERT ({insertCols}) VALUES ({insertVals})");
        sb.AppendLine("OUTPUT $action INTO @act;");

        if (pkIsIdentity)
            sb.AppendLine($"SET IDENTITY_INSERT {targetQualified} OFF;");

        sb.AppendLine(
            "SELECT " +
            "(SELECT COUNT(*) FROM @act WHERE act = 'INSERT') AS Inserted, " +
            "(SELECT COUNT(*) FROM @act WHERE act = 'UPDATE') AS Updated;");

        return sb.ToString();
    }

    /// <summary>
    /// Step C (dry run): read-only counts, no MERGE, no rollback - avoids
    /// trigger/lock surprises while still reporting what a real run would do.
    /// </summary>
    public static string BuildDryRunCounts(TableDefinition table, string targetQualified)
    {
        var pk = B(table.PrimaryKey);
        var diff = BuildDiffPredicate(table, "t", "s");

        return
            "SELECT " +
            "(SELECT COUNT(*) FROM #STG) AS ReadCount, " +
            $"(SELECT COUNT(*) FROM #STG s WHERE NOT EXISTS " +
            $"   (SELECT 1 FROM {targetQualified} t WHERE t.{pk} = s.{pk})) AS Inserted, " +
            $"(SELECT COUNT(*) FROM #STG s INNER JOIN {targetQualified} t " +
            $"   ON t.{pk} = s.{pk} WHERE ({diff})) AS Updated;";
    }

    /// <summary>
    /// Null-safe "any synced non-key column differs" predicate. Treats
    /// (NULL vs value) and (value vs NULL) as a difference; (NULL vs NULL) and
    /// equal values as no difference.
    /// </summary>
    private static string BuildDiffPredicate(TableDefinition table, string tgt, string src)
    {
        var parts = table.NonKeyComparableColumns.Select(c =>
        {
            var col = B(c.SqlName);
            return $"({tgt}.{col} <> {src}.{col}) " +
                   $"OR ({tgt}.{col} IS NULL AND {src}.{col} IS NOT NULL) " +
                   $"OR ({tgt}.{col} IS NOT NULL AND {src}.{col} IS NULL)";
        }).ToList();

        return parts.Count == 0 ? "1 = 0" : string.Join(" OR ", parts.Select(p => $"({p})"));
    }
}
