using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using SyncExamSubJob.Configuration;
using SyncExamSubJob.Logging;
using SyncExamSubJob.Models;
using SyncExamSubJob.Sql;

namespace SyncExamSubJob.Services;

/// <summary>
/// Syncs one table: Steps A-D from the plan.
///   A  RUNNING row (own connection, committed) - durable audit.
///   B  stage from Oracle into #STG (same B/C connection, autocommit).
///   C  upsert: real run = one LOCAL transaction MERGE; dry run = read-only counts.
///   D  finalise run_log to SUCCESS or FAILED (own connection, separate from C).
/// Never throws; returns a <see cref="SyncResult"/> the orchestrator aggregates.
/// </summary>
public sealed class TableSyncService
{
    private readonly SyncConfig _config;
    private readonly FileLogger _log;
    private readonly RunLogRepository _runLog;
    private readonly SchemaInspector _schema;

    public TableSyncService(
        SyncConfig config, FileLogger log, RunLogRepository runLog, SchemaInspector schema)
    {
        _config = config;
        _log = log;
        _runLog = runLog;
        _schema = schema;
    }

    public SyncResult Sync(TableDefinition table, DateTime oracleCut)
    {
        var prevWatermark =
            _runLog.GetLastSuccessfulWatermark(table.Name) ?? _config.InitialWatermarkValue;
        var lowerBound = prevWatermark.AddMinutes(-_config.SafetyOverlapMinutes);
        var runStart = DateTime.Now;

        var runLogId = _runLog.InsertRunning(
            table.Name, runStart, prevWatermark, oracleCut, _config.DryRun);

        _log.Info(
            $"[{table.Name}] start (dryRun={_config.DryRun}) prevWatermark=" +
            $"{Fmt(prevWatermark)} lowerBound={Fmt(lowerBound)} cut={Fmt(oracleCut)}");

        try
        {
            var stagedCols = _schema.GetStagedColumnDefinitions(
                _config.TargetSchema, table.SqlTable,
                table.StagingColumns.Select(c => c.SqlName).ToList());

            var pkIsIdentity = _schema.IsIdentityColumn(
                _config.TargetSchema, table.SqlTable, table.PrimaryKey);

            var lowerLit = SqlText.OracleDateLiteral(lowerBound, _config.DateLiteralMode);
            var cutLit = SqlText.OracleDateLiteral(oracleCut, _config.DateLiteralMode);

            var targetQualified =
                $"{IdentifierValidator.Bracket(_config.TargetSchema)}." +
                $"{IdentifierValidator.Bracket(table.SqlTable)}";

            int rowsRead, inserted, updated;

            using (var conn = new SqlConnection(_config.SqlServerConnectionString))
            {
                conn.Open();

                // Step B - stage from Oracle (autocommit, same connection as C).
                var stagingSql = SqlBuilder.BuildStaging(
                    table, stagedCols, _config.LinkedServerName, _config.OracleSchema,
                    lowerLit, cutLit);
                ExecNonQuery(conn, null, stagingSql);

                rowsRead = ExecScalarInt(conn, null, SqlBuilder.BuildStagedRowCount());

                // Step C - upsert.
                if (_config.DryRun)
                {
                    var (ins, upd) = ReadInsUpd(
                        conn, null, SqlBuilder.BuildDryRunCounts(table, targetQualified));
                    inserted = ins;
                    updated = upd;
                }
                else
                {
                    using var tx = conn.BeginTransaction();
                    try
                    {
                        var mergeSql = SqlBuilder.BuildMergeBatch(
                            table, targetQualified, pkIsIdentity);
                        var (ins, upd) = ReadInsUpd(conn, tx, mergeSql);
                        tx.Commit();
                        inserted = ins;
                        updated = upd;
                    }
                    catch
                    {
                        SafeRollback(tx);
                        throw;
                    }
                }
            }

            var unchanged = rowsRead - inserted - updated;
            if (unchanged < 0) unchanged = 0;

            _runLog.UpdateSuccess(
                runLogId, DateTime.Now, rowsRead, inserted, updated, unchanged, oracleCut);

            _log.Info(
                $"[{table.Name}] SUCCESS read={rowsRead} inserted={inserted} " +
                $"updated={updated} unchanged={unchanged}" +
                (_config.DryRun ? " (DRY RUN - no changes written)" : ""));

            return SyncResult.Ok(table.Name, rowsRead, inserted, updated, unchanged);
        }
        catch (Exception ex)
        {
            _log.Error($"[{table.Name}] FAILED", ex);
            try
            {
                _runLog.UpdateFailed(runLogId, DateTime.Now, ex.ToString());
            }
            catch (Exception logEx)
            {
                _log.Error($"[{table.Name}] could not write FAILED run_log row", logEx);
            }
            return SyncResult.Fail(table.Name, ex.Message);
        }
    }

    private SqlCommand NewCommand(SqlConnection conn, SqlTransaction? tx, string sql)
    {
        var cmd = new SqlCommand(sql, conn) { CommandTimeout = _config.CommandTimeoutSeconds };
        if (tx is not null) cmd.Transaction = tx;
        return cmd;
    }

    private void ExecNonQuery(SqlConnection conn, SqlTransaction? tx, string sql)
    {
        using var cmd = NewCommand(conn, tx, sql);
        cmd.ExecuteNonQuery();
    }

    private int ExecScalarInt(SqlConnection conn, SqlTransaction? tx, string sql)
    {
        using var cmd = NewCommand(conn, tx, sql);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private (int Inserted, int Updated) ReadInsUpd(SqlConnection conn, SqlTransaction? tx, string sql)
    {
        using var cmd = NewCommand(conn, tx, sql);
        using var r = cmd.ExecuteReader();
        if (!r.Read())
            throw new InvalidOperationException("Upsert/dry-run count query returned no row.");
        var ins = Convert.ToInt32(r["Inserted"], CultureInfo.InvariantCulture);
        var upd = Convert.ToInt32(r["Updated"], CultureInfo.InvariantCulture);
        return (ins, upd);
    }

    private static void SafeRollback(SqlTransaction tx)
    {
        try { tx.Rollback(); } catch { /* connection will be disposed regardless */ }
    }

    private static string Fmt(DateTime dt) =>
        dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
