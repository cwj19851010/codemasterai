using CodeMaster.Agent.Services;

namespace CodeMaster.Agent.Tests.Services;

/// <summary>
/// CodeMaster does not configure a proxy — a system-level proxy such as a TUN device applies
/// to every process already. What these tests pin down is the observation side: a failed model
/// call must report which host was dialled and how the request left the machine, otherwise a
/// TLS failure has no actionable detail.
/// </summary>
public sealed class AiNetworkRouteTests
{
    [Theory]
    [InlineData("https://api.example.com/v1", "api.example.com")]
    [InlineData("https://api.example.com/v1/", "api.example.com")]
    [InlineData("https://api.example.com", "api.example.com")]
    [InlineData("http://127.0.0.1:11434/v1", "127.0.0.1")]
    public void Endpoint_resolution_accepts_common_base_url_shapes(string baseUrl, string expectedHost)
    {
        var endpoint = AiNetworkRoute.TryResolveEndpoint(baseUrl);

        Assert.NotNull(endpoint);
        Assert.Equal(expectedHost, endpoint!.Host);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("api.example.com/v1")]
    public void Endpoint_resolution_rejects_unusable_base_urls(string? baseUrl)
    {
        Assert.Null(AiNetworkRoute.TryResolveEndpoint(baseUrl));
    }

    [Fact]
    public void Missing_endpoint_is_reported_instead_of_throwing()
    {
        Assert.Equal("目标地址无法解析，未发起请求。", AiNetworkRoute.Describe(null));
    }

    [Fact]
    public void Route_description_always_names_the_target_host()
    {
        // The machine's own proxy settings decide whether this reports "direct" or a proxy, so
        // only assert the part that must hold either way: the host that was dialled.
        var description = AiNetworkRoute.Describe(new Uri("https://api.example.com/v1/chat"));

        Assert.Contains("api.example.com", description);
        Assert.False(string.IsNullOrWhiteSpace(description));
    }

    [Fact]
    public void Route_description_never_throws_for_a_local_endpoint()
    {
        var description = AiNetworkRoute.Describe(new Uri("http://127.0.0.1:11434/v1/chat"));

        Assert.Contains("127.0.0.1", description);
    }
}
