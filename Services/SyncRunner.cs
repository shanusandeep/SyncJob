using SyncExamSubJob.Configuration;
using SyncExamSubJob.Logging;
using SyncExamSubJob.Models;

namespace SyncExamSubJob.Services;

/// <summary>
/// Orchestrates the four tables in parent -> child dependency order. If a parent
/// fails (or was skipped), every dependent child is SKIPPED for the run, because
/// the child's new rows may reference parent rows that were not synced
/// (FK risk). A failed/skipped table does not advance its watermark, so it
/// re-pulls its full delta next run.
/// </summary>
public sealed class SyncRunner
{
    private readonly SyncConfig _config;
    private readonly FileLogger _log;
    private readonly RunLogRepository _runLog;
    private readonly TableSyncService _tableSync;

    public SyncRunner(
        SyncConfig config, FileLogger log, RunLogRepository runLog, TableSyncService tableSync)
    {
        _config = config;
        _log = log;
        _runLog = runLog;
        _tableSync = tableSync;
    }

    /// <summary>Returns true only if every table succeeded.</summary>
    public bool Run(DateTime oracleCut)
    {
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allOk = true;

        foreach (var table in TableDefinitions.InDependencyOrder)
        {
            var failedDeps = table.Dependencies
                .Where(d => blocked.Contains(d))
                .ToList();

            if (failedDeps.Count > 0)
            {
                var reason =
                    $"Skipped: dependency not synced this run ({string.Join(", ", failedDeps)}).";
                _log.Warn($"[{table.Name}] {reason}");

                var prev = _runLog.GetLastSuccessfulWatermark(table.Name)
                           ?? _config.InitialWatermarkValue;
                try
                {
                    _runLog.InsertSkipped(
                        table.Name, DateTime.Now, prev, oracleCut, _config.DryRun, reason);
                }
                catch (Exception ex)
                {
                    _log.Error($"[{table.Name}] could not write SKIPPED run_log row", ex);
                }

                blocked.Add(table.Name);
                allOk = false;
                continue;
            }

            var result = _tableSync.Sync(table, oracleCut);
            if (result.Status != SyncStatus.Success)
            {
                blocked.Add(table.Name);
                allOk = false;
            }
        }

        return allOk;
    }
}
