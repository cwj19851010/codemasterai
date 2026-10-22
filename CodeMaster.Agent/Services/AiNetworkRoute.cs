using System.Net;

namespace CodeMaster.Agent.Services;

/// <summary>
/// Reports the route an outgoing model call actually takes. This is observation only.
/// <para>
/// CodeMaster deliberately does not configure a proxy. The user's own system-level proxy
/// (typically a TUN device) already applies to every process on the machine, so there is
/// nothing for the application to manage and no setting to keep in sync. But when a model
/// call fails, the first question is always whether the request even left the machine — and
/// that is what this answers.
/// </para>
/// </summary>
internal static class AiNetworkRoute
{
    /// <summary>
    /// Human readable description of the route used for <paramref name="endpoint"/>.
    /// Never throws, because it runs inside failure reporting.
    /// </summary>
    public static string Describe(Uri? endpoint)
    {
        if (endpoint == null)
            return "目标地址无法解析，未发起请求。";

        try
        {
            var effective = HttpClient.DefaultProxy?.GetProxy(endpoint);
            // A bypassed or directly reachable address comes back as the endpoint itself.
            if (effective == null || IsSelf(endpoint, effective))
                return $"直连 → {endpoint.Host}:{endpoint.Port}";

            return $"经 {effective.Scheme}://{effective.Host}:{effective.Port} → {endpoint.Host}:{endpoint.Port}";
        }
        catch (Exception ex)
        {
            return $"网络出口无法解析：{ex.Message}";
        }
    }

    /// <summary>Best-effort endpoint extraction for diagnostics; null when unusable.</summary>
    public static Uri? TryResolveEndpoint(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        var candidate = baseUrl.Trim().TrimEnd('/');
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            return uri;

        return Uri.TryCreate(candidate + "/", UriKind.Absolute, out var withSlash) ? withSlash : null;
    }

    private static bool IsSelf(Uri endpoint, Uri effective)
        => string.Equals(effective.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase) &&
           effective.Port == endpoint.Port;
}
