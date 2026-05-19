using SyncExamSubJob.Configuration;

namespace SyncExamSubJob.Models;

/// <summary>
/// One column of a synced table.
/// - <see cref="OracleExpression"/> = null  -> SQL-Server-only column. Not read from
///   Oracle, not in #STG, inserted as NULL, never updated (e.g. EMY_EMAIL).
/// - Otherwise the column is sourced from Oracle; <see cref="OracleExpression"/> is
///   the Oracle column name (usually identical to <see cref="SqlName"/>, but can be
///   a rename such as Oracle SIGNATURE_FILE_LINK -> SQL EMY_SIGNATURE_FILE_LINK).
/// </summary>
public sealed class ColumnDef
{
    public string SqlName { get; }
    public string? OracleExpression { get; }
    public bool IsPrimaryKey { get; }

    public bool IsLiteralNull => OracleExpression is null;

    private ColumnDef(string sqlName, string? oracleExpression, bool isPrimaryKey)
    {
        SqlName = sqlName;
        OracleExpression = oracleExpression;
        IsPrimaryKey = isPrimaryKey;
    }

    public static ColumnDef Key(string name) => new(name, name, isPrimaryKey: true);
    public static ColumnDef Col(string name) => new(name, name, isPrimaryKey: false);
    public static ColumnDef Mapped(string sqlName, string oracleColumn) =>
        new(sqlName, oracleColumn, isPrimaryKey: false);
    public static ColumnDef LiteralNull(string sqlName) =>
        new(sqlName, null, isPrimaryKey: false);
}

/// <summary>
/// Definition of one table to sync: SQL Server / Oracle names, primary key,
/// change-detection columns, full column mapping and parent dependencies.
/// </summary>
public sealed class TableDefinition
{
    public string Name { get; }            // logical name + SYNC_RUN_LOG.TABLE_NAME
    public string SqlTable { get; }        // SQL Server table name
    public string OracleTable { get; }     // Oracle table name
    public string PrimaryKey { get; }
    public string CreateDtmColumn { get; } // Oracle column used for change detection
    public string ModifyDtmColumn { get; } // Oracle column used for change detection
    public IReadOnlyList<ColumnDef> Columns { get; }
    public IReadOnlyList<string> Dependencies { get; }

    public TableDefinition(
        string name,
        string sqlTable,
        string oracleTable,
        string primaryKey,
        string createDtmColumn,
        string modifyDtmColumn,
        IReadOnlyList<ColumnDef> columns,
        IReadOnlyList<string> dependencies)
    {
        Name = name;
        SqlTable = sqlTable;
        OracleTable = oracleTable;
        PrimaryKey = primaryKey;
        CreateDtmColumn = createDtmColumn;
        ModifyDtmColumn = modifyDtmColumn;
        Columns = columns;
        Dependencies = dependencies;
    }

    /// <summary>Columns present in #STG (everything sourced from Oracle).</summary>
    public IEnumerable<ColumnDef> StagingColumns => Columns.Where(c => !c.IsLiteralNull);

    /// <summary>Non-key Oracle-sourced columns: used for the diff predicate and UPDATE SET.</summary>
    public IEnumerable<ColumnDef> NonKeyComparableColumns =>
        Columns.Where(c => !c.IsLiteralNull && !c.IsPrimaryKey);

    /// <summary>
    /// Whitelist-validate every identifier and assert structural invariants.
    /// Throws <see cref="ConfigurationException"/> on any problem.
    /// </summary>
    public void Validate()
    {
        IdentifierValidator.Require(Name, $"Table[{Name}].Name");
        IdentifierValidator.Require(SqlTable, $"Table[{Name}].SqlTable");
        IdentifierValidator.Require(OracleTable, $"Table[{Name}].OracleTable");
        IdentifierValidator.Require(PrimaryKey, $"Table[{Name}].PrimaryKey");
        IdentifierValidator.Require(CreateDtmColumn, $"Table[{Name}].CreateDtmColumn");
        IdentifierValidator.Require(ModifyDtmColumn, $"Table[{Name}].ModifyDtmColumn");

        if (Columns.Count == 0)
            throw new ConfigurationException($"Table[{Name}] has no columns.");

        foreach (var c in Columns)
        {
            IdentifierValidator.Require(c.SqlName, $"Table[{Name}].Column.SqlName");
            if (c.OracleExpression is not null)
                IdentifierValidator.Require(c.OracleExpression, $"Table[{Name}].Column[{c.SqlName}].OracleExpression");
        }

        var keys = Columns.Where(c => c.IsPrimaryKey).ToList();
        if (keys.Count != 1)
            throw new ConfigurationException($"Table[{Name}] must have exactly one primary-key column.");
        if (!string.Equals(keys[0].SqlName, PrimaryKey, StringComparison.OrdinalIgnoreCase))
            throw new ConfigurationException(
                $"Table[{Name}] PrimaryKey '{PrimaryKey}' does not match the key column '{keys[0].SqlName}'.");
        if (keys[0].IsLiteralNull)
            throw new ConfigurationException($"Table[{Name}] primary key cannot be a literal-null column.");

        var staged = StagingColumns.Select(c => c.SqlName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!staged.Contains(CreateDtmColumn))
            throw new ConfigurationException(
                $"Table[{Name}] CreateDtmColumn '{CreateDtmColumn}' must be an Oracle-sourced column.");
        if (!staged.Contains(ModifyDtmColumn))
            throw new ConfigurationException(
                $"Table[{Name}] ModifyDtmColumn '{ModifyDtmColumn}' must be an Oracle-sourced column.");

        var dupes = Columns.GroupBy(c => c.SqlName, StringComparer.OrdinalIgnoreCase)
                           .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0)
            throw new ConfigurationException(
                $"Table[{Name}] has duplicate columns: {string.Join(", ", dupes)}.");
    }
}
