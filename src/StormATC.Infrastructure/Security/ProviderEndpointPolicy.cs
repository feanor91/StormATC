namespace StormATC.Infrastructure.Security;

/// <summary>
/// Pins credential-bearing provider requests to the official HTTPS endpoints.
/// </summary>
/// <remarks>
/// Provider URLs remain configurable for diagnostics, but a local settings file, environment
/// variable, or command-line argument must never be able to redirect an API key to another host.
/// Every client checks this policy immediately before reading a secret and attaching it to a
/// request. Redirects are disabled separately on the provider HTTP clients.
/// </remarks>
public static class ProviderEndpointPolicy
{
    /// <summary>True only for OpenRouter's official versioned API root.</summary>
    public static bool IsAllowedOpenRouter(string? value) =>
        IsAllowed(value, "https", "openrouter.ai", "/api/v1");

    /// <summary>True only for OpenAI's official versioned API root.</summary>
    public static bool IsAllowedOpenAI(string? value) =>
        IsAllowed(value, "https", "api.openai.com", "/v1");

    /// <summary>True only for Groq's official OpenAI-compatible API root.</summary>
    public static bool IsAllowedGroq(string? value) =>
        IsAllowed(value, "https", "api.groq.com", "/openai/v1");

    /// <summary>True only for DeepSeek's official API root.</summary>
    public static bool IsAllowedDeepSeek(string? value) =>
        IsAllowed(value, "https", "api.deepseek.com", "/");

    /// <summary>True only for Anthropic's official API root.</summary>
    public static bool IsAllowedAnthropic(string? value) =>
        IsAllowed(value, "https", "api.anthropic.com", "/");

    /// <summary>True only for Deepgram's official REST API root.</summary>
    public static bool IsAllowedDeepgramRest(string? value) =>
        IsAllowed(value, "https", "api.deepgram.com", "/");

    /// <summary>True only for Deepgram's official streaming transcription endpoint.</summary>
    public static bool IsAllowedDeepgramStreaming(string? value) =>
        IsAllowed(value, "wss", "api.deepgram.com", "/v1/listen");

    /// <summary>True only for SimBrief's official OFP endpoint.</summary>
    public static bool IsAllowedSimBrief(string? value) =>
        IsAllowed(value, "https", "www.simbrief.com", "/api/xml.fetcher.php");

    private static bool IsAllowed(
        string? value,
        string requiredScheme,
        string requiredHost,
        string requiredPath)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !uri.Scheme.Equals(requiredScheme, StringComparison.OrdinalIgnoreCase)
            || !uri.IdnHost.Equals(requiredHost, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        string path = uri.AbsolutePath.TrimEnd('/');
        string expected = requiredPath.TrimEnd('/');
        return path.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
