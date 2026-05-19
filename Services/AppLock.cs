using System.Data;
using Microsoft.Data.SqlClient;

namespace SyncExamSubJob.Services;

/// <summary>
/// Single-instance guard. Holds a dedicated SQL connection open for the whole
/// run and takes a session-scoped exclusive application lock via the built-in
/// sp_getapplock. If another instance already holds it, acquisition fails
/// immediately (LockTimeout = 0) and the run aborts so two nightly jobs can
/// never overlap. The lock is released on Dispose / connection close.
/// </summary>
public sealed class AppLock : IDisposable
{
    private const string ResourceName = "SyncExamSubJob";

    private readonly SqlConnection _connection;
    private bool _acquired;
    private bool _disposed;

    private AppLock(SqlConnection connection) => _connection = connection;

    /// <summary>
    /// Opens the lock connection and attempts to acquire the lock.
    /// Returns the live <see cref="AppLock"/> (caller must Dispose) and whether
    /// the lock was granted. On failure to grant, the caller should abort.
    /// </summary>
    public static AppLock TryAcquire(string connectionString, int commandTimeoutSeconds, out bool acquired)
    {
        var connection = new SqlConnection(connectionString);
        var appLock = new AppLock(connection);
        try
        {
            connection.Open();

            using var cmd = new SqlCommand("sp_getapplock", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = commandTimeoutSeconds
            };
            cmd.Parameters.AddWithValue("@Resource", ResourceName);
            cmd.Parameters.AddWithValue("@LockMode", "Exclusive");
            cmd.Parameters.AddWithValue("@LockOwner", "Session");
            cmd.Parameters.AddWithValue("@LockTimeout", 0);
            var ret = new SqlParameter("@ReturnValue", SqlDbType.Int)
            {
                Direction = ParameterDirection.ReturnValue
            };
            cmd.Parameters.Add(ret);

            cmd.ExecuteNonQuery();

            // >= 0 : granted (0 immediately, 1 after wait). < 0 : not granted.
            var code = ret.Value is int i ? i : -999;
            appLock._acquired = code >= 0;
            acquired = appLock._acquired;
            return appLock;
        }
        catch
        {
            appLock.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_acquired && _connection.State == ConnectionState.Open)
            {
                using var cmd = new SqlCommand("sp_releaseapplock", _connection)
                {
                    CommandType = CommandType.StoredProcedure
                };
                cmd.Parameters.AddWithValue("@Resource", ResourceName);
                cmd.Parameters.AddWithValue("@LockOwner", "Session");
                cmd.ExecuteNonQuery();
            }
        }
        catch
        {
            // Releasing is best-effort; closing the connection drops the
            // session lock anyway.
        }
        finally
        {
            _connection.Dispose();
        }
    }
}
