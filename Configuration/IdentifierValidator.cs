using System.Text.RegularExpressions;

namespace SyncExamSubJob.Configuration;

/// <summary>
/// Strict whitelist validation for every SQL identifier that originates from
/// configuration or the table definitions. SQL is built by string composition
/// (OPENQUERY cannot use parameters), so identifiers must be proven safe before
/// they are ever concatenated into a command. Anything outside [A-Za-z0-9_] is
/// rejected. Validated identifiers are bracket-quoted for SQL Server via
/// <see cref="Bracket"/> and inlined as-is for Oracle (already whitelisted).
/// </summary>
public static partial class IdentifierValidator
{
    [GeneratedRegex(@"^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && IdentifierPattern().IsMatch(value);

    /// <summary>Throws <see cref="ConfigurationException"/> if the identifier is not whitelist-safe.</summary>
    public static void Require(string? value, string settingName)
    {
        if (!IsValid(value))
        {
            throw new ConfigurationException(
                $"{settingName} value '{value}' is not a valid SQL identifier. " +
                "Only letters, digits and underscore are allowed.");
        }
    }

    /// <summary>Bracket-quote a previously validated SQL Server identifier.</summary>
    public static string Bracket(string validatedIdentifier) => $"[{validatedIdentifier}]";
}
