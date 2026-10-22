using CodeMaster.Agent.Services;

namespace CodeMaster.Agent.Tests.Services;

public sealed class AiProjectFailureEvidenceTests
{
    [Fact]
    public void Build_redacts_credentials_and_creates_a_stable_signature()
    {
        var input = new AiProjectFailureEvidenceInput
        {
            ExecutionMode = "Client",
            ActionName = "buildProject",
            ErrorMessage = "npm build failed password=hunter2 Authorization: Bearer abc.def.ghi",
            DiagnosticOutput = "api_key=secret-value vite build failed at line 42"
        };

        var first = AiProjectFailureEvidenceBuilder.Build(input);
        var second = AiProjectFailureEvidenceBuilder.Build(input);
        var serialized = AiProjectFailureEvidenceBuilder.Serialize(first);

        Assert.Equal(first.FailureSignature, second.FailureSignature);
        Assert.Equal("GeneratedProjectIntegration", first.Category);
        Assert.DoesNotContain("hunter2", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def.ghi", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_does_not_claim_generator_defect_without_owned_source_evidence()
    {
        var unproven = AiProjectFailureEvidenceBuilder.Build(new AiProjectFailureEvidenceInput
        {
            ActionName = "buildProject",
            ErrorMessage = "generated project compiler error in template output"
        });
        var reproduced = AiProjectFailureEvidenceBuilder.Build(new AiProjectFailureEvidenceInput
        {
            ActionName = "buildProject",
            ErrorMessage = "Scriban failed at D:/repo/CodeMaster.Application/Services/CodeGen/TemplateCodeGenerator.cs"
        });

        Assert.Equal("GeneratedProjectIntegration", unproven.Category);
        Assert.Equal("GeneratorOrTemplate", reproduced.Category);
    }

    [Fact]
    public void Build_classifies_metadata_validation_before_generation()
    {
        var evidence = AiProjectFailureEvidenceBuilder.Build(new AiProjectFailureEvidenceInput
        {
            ActionName = "validate",
            ErrorMessage = "Field configuration validation failed: relation target is missing required."
        });

        Assert.Equal("PlanOrMetadata", evidence.Category);
    }
}
