namespace SyncExamSubJob.Models;

public enum SyncStatus
{
    Success,
    Failed
}

/// <summary>Outcome of one table's sync, returned to the orchestrator.</summary>
public sealed record SyncResult(
    string TableName,
    SyncStatus Status,
    int RowsRead,
    int RowsInserted,
    int RowsUpdated,
    int RowsUnchanged,
    string? Error)
{
    public static SyncResult Ok(string table, int read, int ins, int upd, int unchanged) =>
        new(table, SyncStatus.Success, read, ins, upd, unchanged, null);

    public static SyncResult Fail(string table, string error) =>
        new(table, SyncStatus.Failed, 0, 0, 0, 0, error);
}
