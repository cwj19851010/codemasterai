using CodeMaster.Agent.Services;
using CodeMaster.Domain.Entities.Ai;

namespace CodeMaster.Agent.Tests.Services;

/// <summary>
/// A capability probe result ("this model can call tools") is only meaningful for the exact
/// connection it was measured against. Editing where or how requests are sent must therefore
/// drop the result, otherwise an unverified configuration silently inherits the previous pass
/// and generation is attempted against an untested endpoint.
/// </summary>
public sealed class AiProviderCapabilityInvalidationTests
{
    private static SysAiProvider BuildProvider() => new()
    {
        Id = 1,
        UserId = 1,
        Name = "deepseek",
        ProviderType = "OpenAICompatible",
        ExecutionMode = "Server",
        CompatibilityMode = "Standard",
        BaseUrl = "https://api.example.com/v1",
        ModelName = "model-a",
        ExtraHeadersJson = null,
        ApiKeyCipherText = "cipher-a",
        IsEnabled = true,
        // A previously passing probe, which is what must survive / be dropped.
        SupportsTools = true,
        SupportsStreaming = true,
        LastTestStatus = "Success"
    };

    // Captures the fingerprint the way UpdateAsync does, then applies a mutation.
    private static bool RetestRequiredAfter(Action<SysAiProvider> mutate)
    {
        var entity = BuildProvider();
        var previous = AiProviderService.DescribeConnection(entity);
        mutate(entity);
        return AiProviderService.RequiresCapabilityRetest(previous, entity);
    }

    [Fact]
    public void Unchanged_configuration_keeps_the_existing_probe_result()
    {
        Assert.False(RetestRequiredAfter(_ => { }));
    }

    [Fact]
    public void Renaming_a_configuration_keeps_the_existing_probe_result()
    {
        // A label change does not affect the connection, so forcing a retest would be noise.
        Assert.False(RetestRequiredAfter(entity => entity.Name = "renamed"));
    }

    [Fact]
    public void Toggling_default_or_enabled_flags_keeps_the_existing_probe_result()
    {
        Assert.False(RetestRequiredAfter(entity =>
        {
            entity.IsDefault = true;
            entity.IsEnabled = false;
        }));
    }

    [Theory]
    [InlineData("https://other.example.com/v1")]
    [InlineData("https://api.example.com/v2")]
    public void Changing_the_endpoint_requires_a_retest(string baseUrl)
    {
        Assert.True(RetestRequiredAfter(entity => entity.BaseUrl = baseUrl));
    }

    [Fact]
    public void Changing_the_model_requires_a_retest()
    {
        Assert.True(RetestRequiredAfter(entity => entity.ModelName = "model-b"));
    }

    [Fact]
    public void Changing_the_api_key_requires_a_retest()
    {
        // The key is re-encrypted whenever the user submits a new one, so a different
        // ciphertext is the signal that credentials changed.
        Assert.True(RetestRequiredAfter(entity => entity.ApiKeyCipherText = "cipher-b"));
    }

    [Fact]
    public void Clearing_the_api_key_requires_a_retest()
    {
        Assert.True(RetestRequiredAfter(entity => entity.ApiKeyCipherText = null));
    }

    [Theory]
    [InlineData("Anthropic")]
    public void Changing_the_protocol_requires_a_retest(string providerType)
    {
        Assert.True(RetestRequiredAfter(entity => entity.ProviderType = providerType));
    }

    [Fact]
    public void Changing_the_execution_mode_requires_a_retest()
    {
        Assert.True(RetestRequiredAfter(entity => entity.ExecutionMode = "Local"));
    }

    [Fact]
    public void Changing_the_compatibility_mode_requires_a_retest()
    {
        Assert.True(RetestRequiredAfter(entity => entity.CompatibilityMode = "OpenRouterReasoning"));
    }

    [Fact]
    public void Changing_extra_headers_requires_a_retest()
    {
        Assert.True(RetestRequiredAfter(entity => entity.ExtraHeadersJson = "{\"X-Tenant\":\"a\"}"));
    }

    [Fact]
    public void Fingerprint_does_not_confuse_field_boundaries()
    {
        // Concatenating without a separator would make these two indistinguishable.
        var left = BuildProvider();
        left.BaseUrl = "https://api.example.com/v1a";
        left.ModelName = "b";

        var right = BuildProvider();
        right.BaseUrl = "https://api.example.com/v1";
        right.ModelName = "ab";

        Assert.NotEqual(
            AiProviderService.DescribeConnection(left),
            AiProviderService.DescribeConnection(right));
    }

    [Fact]
    public void Reset_clears_both_capability_flags_and_the_test_record()
    {
        var entity = BuildProvider();
        entity.LastTestAt = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
        entity.LastTestMessage = "CODEMASTER_OK";

        AiProviderService.ResetCapabilityProbe(entity);

        Assert.False(entity.SupportsTools);
        Assert.False(entity.SupportsStreaming);
        Assert.Null(entity.LastTestAt);
        Assert.Null(entity.LastTestStatus);
        Assert.Null(entity.LastTestMessage);
    }

    [Fact]
    public void Reset_then_detecting_an_unchanged_configuration_stays_consistent()
    {
        // Guard the order used by UpdateAsync: snapshot, mutate, compare, reset.
        var entity = BuildProvider();
        var previous = AiProviderService.DescribeConnection(entity);

        entity.BaseUrl = "https://moved.example.com/v1";
        Assert.True(AiProviderService.RequiresCapabilityRetest(previous, entity));

        AiProviderService.ResetCapabilityProbe(entity);
        var afterReset = AiProviderService.DescribeConnection(entity);

        Assert.False(AiProviderService.RequiresCapabilityRetest(afterReset, entity));
    }
}
