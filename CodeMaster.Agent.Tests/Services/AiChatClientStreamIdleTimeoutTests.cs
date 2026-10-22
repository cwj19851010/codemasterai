using CodeMaster.Agent.Services;

namespace CodeMaster.Agent.Tests.Services;

/// <summary>
/// The streaming idle bound used to be a hardcoded 60 seconds, which aborted generation whenever a
/// reasoning model stayed silent longer than that while composing a large plan. It is now
/// configurable with a more realistic fallback.
/// </summary>
public sealed class AiChatClientStreamIdleTimeoutTests
{
    [Fact]
    public void Falls_back_to_the_default_when_unset()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(AiChatClientFactory.DefaultStreamIdleSeconds),
            AiChatClientFactory.ResolveStreamIdleTimeout(null));
    }

    [Fact]
    public void Default_allows_for_a_slow_reasoning_model()
    {
        // The old 60s bound was the whole problem, so guard against it creeping back.
        Assert.True(AiChatClientFactory.DefaultStreamIdleSeconds >= 300);
    }

    [Theory]
    [InlineData("120", 120)]
    [InlineData("600", 600)]
    [InlineData("1", 1)]
    public void Honours_a_valid_override(string raw, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), AiChatClientFactory.ResolveStreamIdleTimeout(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-30")]
    public void Falls_back_to_the_default_for_unusable_values(string raw)
    {
        Assert.Equal(
            TimeSpan.FromSeconds(AiChatClientFactory.DefaultStreamIdleSeconds),
            AiChatClientFactory.ResolveStreamIdleTimeout(raw));
    }

    [Fact]
    public void Exposes_the_expected_environment_variable_name()
    {
        Assert.Equal("CODEMASTER_STREAM_IDLE_TIMEOUT_SECONDS", AiChatClientFactory.StreamIdleTimeoutEnvVar);
    }
}
