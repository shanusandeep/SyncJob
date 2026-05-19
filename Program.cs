using Microsoft.Extensions.Configuration;
using SyncExamSubJob.Configuration;
using SyncExamSubJob.Logging;
using SyncExamSubJob.Models;
using SyncExamSubJob.Services;

// Process exit codes (read by Task Scheduler "Last Run Result"):
//   0 = all tables synced successfully
//   1 = one or more tables failed or were skipped
//   2 = another instance is already running (lock not granted)
//   3 = fatal/configuration error before the run could complete
const int ExitSuccess = 0;
const int ExitTablesFailed = 1;
const int ExitLockNotAcquired = 2;
const int ExitFatal = 3;

FileLogger? logger = null;
try
{
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
        .Build();

    var cfg = configuration.GetSection("Sync").Get<SyncConfig>()
              ?? throw new ConfigurationException("Missing 'Sync' section in appsettings.json.");

    cfg.Validate();
    foreach (var t in TableDefinitions.InDependencyOrder)
        t.Validate();

    logger = new FileLogger(cfg.LogDirectory);
    logger.Info("=== SyncExamSubJob started ===");
    logger.Info($"Log file: {logger.LogFilePath}");
    logger.Info(
        $"DryRun={cfg.DryRun} TargetSchema={cfg.TargetSchema} " +
        $"LinkedServer={cfg.LinkedServerName} OracleSchema={cfg.OracleSchema} " +
        $"DateLiteralMode={cfg.DateLiteralMode} SafetyOverlapMinutes={cfg.SafetyOverlapMinutes}");

    using var appLock = AppLock.TryAcquire(
        cfg.SqlServerConnectionString, cfg.CommandTimeoutSeconds, out var acquired);
    if (!acquired)
    {
        logger.Warn("Another instance is already running (application lock not granted). Exiting.");
        return ExitLockNotAcquired;
    }

    var cut = new OracleClock(cfg).CaptureCut();
    logger.Info($"Oracle cut (shared upper watermark) = {cut:yyyy-MM-dd HH:mm:ss.fff}");

    var runLog = new RunLogRepository(cfg);
    runLog.EnsureTable();

    var schema = new SchemaInspector(cfg.SqlServerConnectionString, cfg.CommandTimeoutSeconds);
    var tableSync = new TableSyncService(cfg, logger, runLog, schema);
    var runner = new SyncRunner(cfg, logger, runLog, tableSync);

    var ok = runner.Run(cut);
    logger.Info(ok
        ? "=== SyncExamSubJob completed: ALL TABLES SUCCESS ==="
        : "=== SyncExamSubJob completed WITH FAILURES/SKIPS ===");
    return ok ? ExitSuccess : ExitTablesFailed;
}
catch (ConfigurationException cex)
{
    logger?.Error("Configuration error: " + cex.Message);
    Console.Error.WriteLine("Configuration error: " + cex.Message);
    return ExitFatal;
}
catch (Exception ex)
{
    logger?.Error("Fatal error", ex);
    Console.Error.WriteLine("Fatal error: " + ex);
    return ExitFatal;
}
finally
{
    logger?.Info("=== SyncExamSubJob exiting ===");
    logger?.Dispose();
}
