using System.Text.RegularExpressions;

namespace StormATC.Infrastructure.Security;

/// <summary>Last-line defence against credential-shaped text reaching a local log or export.</summary>
public static partial class SensitiveDataRedactor
{
    private const string Replacement = "[REDACTED]";

    /// <summary>Redacts common authorization headers, API-key JSON fields, and key prefixes.</summary>
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        string redacted = AuthorizationHeaderRegex().Replace(
            value,
            match => match.Groups[1].Value + Replacement);

        redacted = CredentialFieldRegex().Replace(
            redacted,
            match => match.Groups[1].Value + Replacement + match.Groups[3].Value);

        return ProviderKeyRegex().Replace(redacted, Replacement);
    }

    [GeneratedRegex(
        @"(?i)\b(authorization\s*[:=]\s*(?:bearer|token)\s+)[^\s,;""']+",
        RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeaderRegex();

    [GeneratedRegex(
        @"(?i)([""']?(?:api[_-]?key|access[_-]?token|secret|password)[""']?\s*[:=]\s*[""'])([^""'\r\n]+)([""'])",
        RegexOptions.CultureInvariant)]
    private static partial Regex CredentialFieldRegex();

    [GeneratedRegex(
        @"\b(?:sk-or-v1-|sk-)[A-Za-z0-9_-]{12,}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProviderKeyRegex();
}
