using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SyncExamSubJob.Configuration;

namespace SyncExamSubJob.Services;

public sealed record StagedColumn(string Name, string TypeDefinition);

/// <summary>
/// Reads SQL Server metadata so the staging temp table is built from the EXACT
/// target column types (no hardcoded / guessed types, no SELECT * from the
/// linked server) and so IDENTITY primary keys are detected up front.
/// </summary>
public sealed class SchemaInspector
{
    private readonly string _connectionString;
    private readonly int _commandTimeout;

    public SchemaInspector(string connectionString, int commandTimeout)
    {
        _connectionString = connectionString;
        _commandTimeout = commandTimeout;
    }

    /// <summary>
    /// Resolve the SQL Server type definition for each requested column, returned
    /// in the requested order. Throws if a column is missing from the table
    /// (catches Oracle/SQL Server schema drift before any data moves).
    /// </summary>
    public IReadOnlyList<StagedColumn> GetStagedColumnDefinitions(
        string schema, string table, IReadOnlyList<string> columnNames)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        const string sql = @"
SELECT COLUMN_NAME, DATA_TYPE,
       CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE,
       DATETIME_PRECISION, COLLATION_NAME
FROM   INFORMATION_SCHEMA.COLUMNS
WHERE  TABLE_SCHEMA = @schema AND TABLE_NAME = @table;";

        using (var conn = new SqlConnection(_connectionString))
        {
            conn.Open();
            using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _commandTimeout };
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.Parameters.AddWithValue("@table", table);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                // INFORMATION_SCHEMA.COLUMNS column types vary (int / tinyint /
                // smallint), so read every numeric facet via Convert.ToInt32.
                var name = r.GetString(0);
                var dataType = r.GetString(1).ToLowerInvariant();
                int? charLen = r.IsDBNull(2) ? null : Convert.ToInt32(r.GetValue(2), CultureInfo.InvariantCulture);
                int? precision = r.IsDBNull(3) ? null : Convert.ToInt32(r.GetValue(3), CultureInfo.InvariantCulture);
                int? scale = r.IsDBNull(4) ? null : Convert.ToInt32(r.GetValue(4), CultureInfo.InvariantCulture);
                int? dtPrecision = r.IsDBNull(5) ? null : Convert.ToInt32(r.GetValue(5), CultureInfo.InvariantCulture);
                string? collation = r.IsDBNull(6) ? null : r.GetString(6);

                byName[name] = BuildTypeDefinition(dataType, charLen, precision, scale, dtPrecision, collation);
            }
        }

        var result = new List<StagedColumn>(columnNames.Count);
        foreach (var col in columnNames)
        {
            if (!byName.TryGetValue(col, out var typeDef))
            {
                throw new InvalidOperationException(
                    $"Column [{col}] was not found in [{schema}].[{table}]. " +
                    "The SQL Server table does not match the configured definition.");
            }
            result.Add(new StagedColumn(col, typeDef));
        }
        return result;
    }

    /// <summary>True if the given column of the table is an IDENTITY column.</summary>
    public bool IsIdentityColumn(string schema, string table, string column)
    {
        const string sql = @"
SELECT CASE WHEN EXISTS (
    SELECT 1
    FROM sys.identity_columns ic
    JOIN sys.tables   t ON t.object_id = ic.object_id
    JOIN sys.schemas  s ON s.schema_id = t.schema_id
    JOIN sys.columns  c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE s.name = @schema AND t.name = @table AND c.name = @column
) THEN 1 ELSE 0 END;";

        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _commandTimeout };
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);
        cmd.Parameters.AddWithValue("@column", column);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static string BuildTypeDefinition(
        string dataType, int? charLen, int? precision, int? scale, int? dtPrecision,
        string? collation)
    {
        switch (dataType)
        {
            case "char":
            case "varchar":
            case "nchar":
            case "nvarchar":
            {
                var len = charLen == -1 ? "max" : (charLen?.ToString(CultureInfo.InvariantCulture) ?? "1");
                // Pin #STG string columns to the TARGET column collation so the
                // null-safe diff / dry-run comparisons never hit a tempdb-vs-DB
                // collation conflict.
                var collateClause = "";
                if (!string.IsNullOrEmpty(collation))
                {
                    if (!IdentifierValidator.IsValid(collation))
                        throw new InvalidOperationException(
                            $"Unexpected collation name '{collation}' for a {dataType} column.");
                    collateClause = $" COLLATE {collation}";
                }
                return $"{dataType}({len}){collateClause}";
            }

            case "binary":
            case "varbinary":
            {
                var len = charLen == -1 ? "max" : (charLen?.ToString(CultureInfo.InvariantCulture) ?? "1");
                return $"{dataType}({len})";
            }

            case "decimal":
            case "numeric":
                return $"{dataType}({precision ?? 18},{scale ?? 0})";

            case "datetime2":
            case "datetimeoffset":
            case "time":
                return $"{dataType}({dtPrecision ?? 7})";

            default:
                // int, bigint, smallint, tinyint, bit, date, datetime,
                // smalldatetime, money, smallmoney, float, real,
                // uniqueidentifier, etc. - no facets needed for staging.
                return dataType;
        }
    }
}
