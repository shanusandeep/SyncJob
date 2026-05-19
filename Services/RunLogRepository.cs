using System.Data;
using Microsoft.Data.SqlClient;
using SyncExamSubJob.Configuration;

namespace SyncExamSubJob.Services;

/// <summary>
/// Owns the SYNC_RUN_LOG lifecycle. Every method uses its own short-lived
/// connection and autocommits, deliberately OUTSIDE the per-table data
/// transaction, so a data rollback can never erase the RUNNING / FAILED audit
/// row. The next run's lower bound is read only from SUCCESS, non dry-run rows.
/// </summary>
public sealed class RunLogRepository
{
    private readonly string _connectionString;
    private readonly int _commandTimeout;
    private readonly string _qualified; // [schema].[SYNC_RUN_LOG]

    public RunLogRepository(SyncConfig config)
    {
        _connectionString = config.SqlServerConnectionString;
        _commandTimeout = config.CommandTimeoutSeconds;
        _qualified =
            $"{IdentifierValidator.Bracket(config.TargetSchema)}.{IdentifierValidator.Bracket("SYNC_RUN_LOG")}";
    }

    public void EnsureTable()
    {
        var ddl = $@"
IF OBJECT_ID(N'{_qualified}', N'U') IS NULL
BEGIN
    CREATE TABLE {_qualified} (
        RUN_LOG_ID       BIGINT IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_SYNC_RUN_LOG PRIMARY KEY,
        TABLE_NAME       VARCHAR(64)   NOT NULL,
        RUN_START_DTM    DATETIME2(3)  NOT NULL,
        RUN_END_DTM      DATETIME2(3)  NULL,
        PREV_WATERMARK   DATETIME2(7)  NULL,
        ORACLE_WATERMARK DATETIME2(7)  NULL,
        ROWS_READ        INT           NULL,
        ROWS_INSERTED    INT           NULL,
        ROWS_UPDATED     INT           NULL,
        ROWS_UNCHANGED   INT           NULL,
        STATUS           VARCHAR(16)   NOT NULL,
        IS_DRY_RUN       BIT           NOT NULL CONSTRAINT DF_SYNC_RUN_LOG_DRY DEFAULT (0),
        ERROR_MESSAGE    NVARCHAR(MAX) NULL
    );
    CREATE INDEX IX_SYNC_RUN_LOG_TBL_STATUS
        ON {_qualified} (TABLE_NAME, STATUS, IS_DRY_RUN, ORACLE_WATERMARK);
END;";

        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand(ddl, conn) { CommandTimeout = _commandTimeout };
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Last successful, non dry-run Oracle watermark for the table, or null if
    /// the table has never had a successful real run (caller falls back to the
    /// configured InitialWatermark).
    /// </summary>
    public DateTime? GetLastSuccessfulWatermark(string tableName)
    {
        var sql =
            $"SELECT MAX(ORACLE_WATERMARK) FROM {_qualified} " +
            "WHERE TABLE_NAME = @t AND STATUS = 'SUCCESS' AND IS_DRY_RUN = 0;";

        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _commandTimeout };
        cmd.Parameters.AddWithValue("@t", tableName);
        var result = cmd.ExecuteScalar();
        return (result is null || result == DBNull.Value) ? null : (DateTime)result;
    }

    /// <summary>Step A: insert the RUNNING row and return its RUN_LOG_ID.</summary>
    public long InsertRunning(
        string tableName, DateTime runStart, DateTime prevWatermark,
        DateTime oracleWatermark, bool isDryRun)
    {
        var sql =
            $"INSERT INTO {_qualified} " +
            "(TABLE_NAME, RUN_START_DTM, PREV_WATERMARK, ORACLE_WATERMARK, STATUS, IS_DRY_RUN) " +
            "VALUES (@t, @start, @prev, @cut, 'RUNNING', @dry); " +
            "SELECT CAST(SCOPE_IDENTITY() AS bigint);";

        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _commandTimeout };
        cmd.Parameters.AddWithValue("@t", tableName);
        AddDateTime2(cmd, "@start", runStart);
        AddDateTime2(cmd, "@prev", prevWatermark);
        AddDateTime2(cmd, "@cut", oracleWatermark);
        cmd.Parameters.AddWithValue("@dry", isDryRun);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Step D (success): finalise the RUNNING row.</summary>
    public void UpdateSuccess(
        long runLogId, DateTime runEnd, int rowsRead, int rowsInserted,
        int rowsUpdated, int rowsUnchanged, DateTime oracleWatermark)
    {
        var sql =
            $"UPDATE {_qualified} SET " +
            "RUN_END_DTM=@end, ROWS_READ=@read, ROWS_INSERTED=@ins, ROWS_UPDATED=@upd, " +
            "ROWS_UNCHANGED=@unch, ORACLE_WATERMARK=@cut, STATUS='SUCCESS', ERROR_MESSAGE=NULL " +
            "WHERE RUN_LOG_ID=@id;";

        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _commandTimeout };
        AddDateTime2(cmd, "@end", runEnd);
        cmd.Parameters.AddWithValue("@read", rowsRead);
        cmd.Parameters.AddWithValue("@ins", rowsInserted);
        cmd.Parameters.AddWithValue("@upd", rowsUpdated);
        cmd.Parameters.AddWithValue("@unch", rowsUnchanged);
        AddDateTime2(cmd, "@cut", oracleWatermark);
        cmd.Parameters.AddWithValue("@id", runLogId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Step D (failure): record the error on its own committed statement.</summary>
    public void UpdateFailed(long runLogId, DateTime runEnd, string errorMessage)
    {
        var sql =
            $"UPDATE {_qualified} SET RUN_END_DTM=@end, STATUS='FAILED', ERROR_MESSAGE=@err " +
            "WHERE RUN_LOG_ID=@id;";

        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _commandTimeout };
        AddDateTime2(cmd, "@end", runEnd);
        cmd.Parameters.AddWithValue("@err", Truncate(errorMessage, 100_000));
        cmd.Parameters.AddWithValue("@id", runLogId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>A dependency failed: record this table as SKIPPED for the run.</summary>
    public void InsertSkipped(
        string tableName, DateTime runStart, DateTime prevWatermark,
        DateTime oracleWatermark, bool isDryRun, string reason)
    {
        var sql =
            $"INSERT INTO {_qualified} " +
            "(TABLE_NAME, RUN_START_DTM, RUN_END_DTM, PREV_WATERMARK, ORACLE_WATERMARK, " +
            " STATUS, IS_DRY_RUN, ERROR_MESSAGE) " +
            "VALUES (@t, @start, @end, @prev, @cut, 'SKIPPED', @dry, @err);";

        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _commandTimeout };
        cmd.Parameters.AddWithValue("@t", tableName);
        AddDateTime2(cmd, "@start", runStart);
        AddDateTime2(cmd, "@end", runStart);
        AddDateTime2(cmd, "@prev", prevWatermark);
        AddDateTime2(cmd, "@cut", oracleWatermark);
        cmd.Parameters.AddWithValue("@dry", isDryRun);
        cmd.Parameters.AddWithValue("@err", Truncate(reason, 100_000));
        cmd.ExecuteNonQuery();
    }

    private static void AddDateTime2(SqlCommand cmd, string name, DateTime value)
    {
        var p = new SqlParameter(name, SqlDbType.DateTime2) { Value = value };
        cmd.Parameters.Add(p);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);
}
