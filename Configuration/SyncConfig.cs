using System.Globalization;

namespace SyncExamSubJob.Configuration;

/// <summary>
/// Strongly-typed binding of the "Sync" section of appsettings.json.
/// All values are validated in <see cref="Validate"/> before any SQL is built.
/// </summary>
public sealed class SyncConfig
{
    public string SqlServerConnectionString { get; set; } = "";
    public string LinkedServerName { get; set; } = "";
    public string OracleSchema { get; set; } = "";
    public string TargetSchema { get; set; } = "dbo";

    /// <summary>"Timestamp" => TO_TIMESTAMP(...); "Date" => TO_DATE(...). See OracleDateLiteral.</summary>
    public string OracleDateLiteralMode { get; set; } = "Timestamp";

    /// <summary>
    /// Used as the lower bound only when a table has no prior successful (non dry-run) run.
    /// Set to the production cut-over datetime so the first nightly run picks up
    /// changes made after the full migration.
    /// </summary>
    public string InitialWatermark { get; set; } = "";

    public int SafetyOverlapMinutes { get; set; } = 30;
    public int CommandTimeoutSeconds { get; set; } = 1800;
    public int ForeignKeyDiagnosticSampleRows { get; set; } = 50;
    public string LogDirectory { get; set; } = "logs";
    public bool DryRun { get; set; }

    public OracleDateMode DateLiteralMode { get; private set; }
    public DateTime InitialWatermarkValue { get; private set; }

    /// <summary>
    /// Fail fast on any bad configuration before connecting anywhere. Throws
    /// <see cref="ConfigurationException"/> with an actionable message.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(SqlServerConnectionString))
            throw new ConfigurationException("Sync:SqlServerConnectionString is required.");

        IdentifierValidator.Require(LinkedServerName, "Sync:LinkedServerName");
        IdentifierValidator.Require(OracleSchema, "Sync:OracleSchema");
        IdentifierValidator.Require(TargetSchema, "Sync:TargetSchema");

        if (!Enum.TryParse<OracleDateMode>(OracleDateLiteralMode, ignoreCase: true, out var mode))
            throw new ConfigurationException(
                $"Sync:OracleDateLiteralMode '{OracleDateLiteralMode}' is invalid. Use 'Timestamp' or 'Date'.");
        DateLiteralMode = mode;

        if (string.IsNullOrWhiteSpace(InitialWatermark) ||
            !DateTime.TryParse(InitialWatermark, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var iw))
        {
            throw new ConfigurationException(
                $"Sync:InitialWatermark '{InitialWatermark}' is not a valid datetime " +
                "(use e.g. 2026-06-01T00:00:00).");
        }
        InitialWatermarkValue = iw;

        if (SafetyOverlapMinutes < 0)
            throw new ConfigurationException("Sync:SafetyOverlapMinutes must be >= 0.");
        if (CommandTimeoutSeconds <= 0)
            throw new ConfigurationException("Sync:CommandTimeoutSeconds must be > 0.");
        if (ForeignKeyDiagnosticSampleRows < 0)
            throw new ConfigurationException("Sync:ForeignKeyDiagnosticSampleRows must be >= 0.");
        if (string.IsNullOrWhiteSpace(LogDirectory))
            throw new ConfigurationException("Sync:LogDirectory is required.");
    }
}

public enum OracleDateMode
{
    Timestamp = 0,
    Date = 1
}

public sealed class ConfigurationException : Exception
{
    public ConfigurationException(string message) : base(message) { }
}
