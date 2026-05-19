using System.Globalization;
using Microsoft.Data.SqlClient;
using SyncExamSubJob.Configuration;
using SyncExamSubJob.Sql;

namespace SyncExamSubJob.Services;

/// <summary>
/// Captures a single Oracle clock value at the start of the run, shared by every
/// table as the upper watermark bound. Using Oracle's own clock (not the app or
/// SQL Server host) removes cross-server skew; one shared value guarantees a
/// consistent cut, so a parent row created in Oracle after the cut and its child
/// are both excluded this run and picked up together next run (no FK gap).
///
/// CAST(SYSTIMESTAMP AS TIMESTAMP) yields Oracle DB-local time, matching the
/// *_CREATE_DTM / *_MDFY_DTM columns (see plan assumptions).
/// </summary>
public sealed class OracleClock
{
    private readonly string _connectionString;
    private readonly string _linkedServer;
    private readonly int _commandTimeout;

    public OracleClock(SyncConfig config)
    {
        _connectionString = config.SqlServerConnectionString;
        _linkedServer = config.LinkedServerName;
        _commandTimeout = config.CommandTimeoutSeconds;
    }

    public DateTime CaptureCut()
    {
        const string innerOracle = "SELECT CAST(SYSTIMESTAMP AS TIMESTAMP) AS TS FROM DUAL";
        var escaped = SqlText.EscapeForOpenQuery(innerOracle);
        var link = IdentifierValidator.Bracket(_linkedServer);

        var sql =
            $"SELECT CAST(q.TS AS datetime2(7)) AS TS " +
            $"FROM OPENQUERY({link}, '{escaped}') AS q;";

        using var conn = new SqlConnection(_connectionString);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = _commandTimeout };
        var result = cmd.ExecuteScalar();

        if (result is null || result == DBNull.Value)
            throw new InvalidOperationException(
                "Oracle clock query returned no value (linked server unreachable?).");

        return Convert.ToDateTime(result, CultureInfo.InvariantCulture);
    }
}
