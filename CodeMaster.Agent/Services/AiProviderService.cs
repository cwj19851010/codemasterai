using CodeMaster.Agent.Contracts;
using CodeMaster.Core.Repositories;
using CodeMaster.Domain.Entities.Ai;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using SqlSugar;

namespace CodeMaster.Agent.Services;

public interface IAiProviderService
{
    Task<List<AiProviderDto>> GetListAsync(string? executionMode = null);
    Task<AiProviderDto> CreateAsync(SaveAiProviderRequest input);
    Task<AiProviderDto> UpdateAsync(long id, SaveAiProviderRequest input);
    Task<bool> DeleteAsync(long id);
    Task<AiProviderTestResult> TestAsync(long id);
}

internal sealed class AiProviderService : IAiProviderService
{
    private readonly IRepository<SysAiProvider> _repository;
    private readonly IRepository<AiConversation> _conversationRepository;
    private readonly ISqlSugarClient _db;
    private readonly IAiCurrentUser _currentUser;
    private readonly IAiSecretProtector _secretProtector;
    private readonly IAiProviderFactory _providerFactory;

    public AiProviderService(
        IRepository<SysAiProvider> repository,
        IRepository<AiConversation> conversationRepository,
        ISqlSugarClient db,
        IAiCurrentUser currentUser,
        IAiSecretProtector secretProtector,
        IAiProviderFactory providerFactory)
    {
        _repository = repository;
        _conversationRepository = conversationRepository;
        _db = db;
        _currentUser = currentUser;
        _secretProtector = secretProtector;
        _providerFactory = providerFactory;
    }

    public async Task<List<AiProviderDto>> GetListAsync(string? executionMode = null)
    {
        var userId = _currentUser.UserId;
        var query = _repository.GetQueryable()
            .Where(x => x.UserId == userId);

        if (!string.IsNullOrWhiteSpace(executionMode))
        {
            if (executionMode is not (AiExecutionModes.Server or AiExecutionModes.Local))
                throw new ArgumentException("Unsupported AI execution mode.");

            query = query.Where(x => x.ExecutionMode == executionMode);
        }

        var items = await query
            .OrderByDescending(x => x.IsDefault)
            .OrderByDescending(x => x.CreateTime)
            .ToListAsync();

        return items.Select(ToDto).ToList();
    }

    public async Task<AiProviderDto> CreateAsync(SaveAiProviderRequest input)
    {
        Validate(input);
        var entity = new SysAiProvider
        {
            UserId = _currentUser.UserId,
            Name = input.Name.Trim(),
            ProviderType = input.ProviderType,
            ExecutionMode = input.ExecutionMode,
            CompatibilityMode = NormalizeCompatibilityMode(input.CompatibilityMode),
            BaseUrl = NormalizeBaseUrl(input.ProviderType, input.BaseUrl),
            ApiKeyCipherText = ShouldPersistServerSecret(input)
                ? _secretProtector.Protect(input.ApiKey!.Trim())
                : null,
            ModelName = input.ModelName.Trim(),
            ExtraHeadersJson = ShouldPersistServerSettings(input)
                ? NormalizeOptional(input.ExtraHeadersJson)
                : null,
            IsDefault = input.IsDefault,
            IsEnabled = input.IsEnabled,
            CreateUserId = _currentUser.UserId,
            CreateBy = _currentUser.UserName,
            CreateTime = DateTime.UtcNow
        };

        entity.Id = await _repository.InsertAsync(entity);
        if (entity.IsDefault)
        {
            await ClearOtherDefaultsAsync(entity.Id);
        }

        return ToDto(entity);
    }

    public async Task<AiProviderDto> UpdateAsync(long id, SaveAiProviderRequest input)
    {
        Validate(input);
        var entity = await GetOwnedEntityAsync(id);

        // Snapshot what decides where and how requests are sent. A capability result is only
        // meaningful for the exact connection it was measured against, so any change below must
        // invalidate it — otherwise an unverified configuration silently keeps inheriting the
        // previous configuration's pass.
        var previousConnection = DescribeConnection(entity);

        entity.Name = input.Name.Trim();
        entity.ProviderType = input.ProviderType;
        entity.ExecutionMode = input.ExecutionMode;
        entity.CompatibilityMode = NormalizeCompatibilityMode(input.CompatibilityMode);
        entity.BaseUrl = NormalizeBaseUrl(input.ProviderType, input.BaseUrl);
        entity.ModelName = input.ModelName.Trim();
        entity.ExtraHeadersJson = ShouldPersistServerSettings(input)
            ? NormalizeOptional(input.ExtraHeadersJson)
            : null;
        entity.IsDefault = input.IsDefault;
        entity.IsEnabled = input.IsEnabled;
        entity.UpdateUserId = _currentUser.UserId;
        entity.UpdateBy = _currentUser.UserName;
        entity.UpdateTime = DateTime.UtcNow;

        if (input.ExecutionMode == AiExecutionModes.Local)
        {
            // Local credentials belong exclusively to the desktop/sidecar store.
            entity.ApiKeyCipherText = null;
        }
        else if (input.ClearApiKey)
        {
            entity.ApiKeyCipherText = null;
        }
        else if (!string.IsNullOrWhiteSpace(input.ApiKey))
        {
            entity.ApiKeyCipherText = _secretProtector.Protect(input.ApiKey.Trim());
        }

        if (RequiresCapabilityRetest(previousConnection, entity))
            ResetCapabilityProbe(entity);

        await _repository.UpdateAsync(entity);
        if (entity.IsDefault)
        {
            await ClearOtherDefaultsAsync(entity.Id);
        }

        return ToDto(entity);
    }

    /// <summary>
    /// True when the connection changed enough that the stored probe result no longer applies.
    /// Renaming a configuration is deliberately not a change, so a pure label edit keeps the
    /// existing result.
    /// </summary>
    internal static bool RequiresCapabilityRetest(string previousConnection, SysAiProvider entity)
        => !string.Equals(previousConnection, DescribeConnection(entity), StringComparison.Ordinal);

    /// <summary>Current connection fingerprint; capture it before mutating the entity.</summary>
    internal static string DescribeConnection(SysAiProvider entity) => string.Join(
        "\u001f",
        entity.ProviderType ?? string.Empty,
        entity.ExecutionMode ?? string.Empty,
        entity.CompatibilityMode ?? string.Empty,
        entity.BaseUrl ?? string.Empty,
        entity.ModelName ?? string.Empty,
        entity.ExtraHeadersJson ?? string.Empty,
        entity.ApiKeyCipherText ?? string.Empty);

    /// <summary>
    /// Drops the stored probe result so the configuration reads as never tested. The flags are
    /// what gate plan creation, so clearing them forces a fresh test before generation.
    /// </summary>
    internal static void ResetCapabilityProbe(SysAiProvider entity)
    {
        entity.SupportsTools = false;
        entity.SupportsStreaming = false;
        entity.LastTestAt = null;
        entity.LastTestStatus = null;
        entity.LastTestMessage = null;
    }

    public async Task<bool> DeleteAsync(long id)
    {
        _ = await GetOwnedEntityAsync(id);
        var activeConversationCount = await _conversationRepository.GetQueryable()
            .Where(x => x.UserId == _currentUser.UserId && x.ProviderId == id && x.Status == "Active")
            .CountAsync();
        if (activeConversationCount > 0)
        {
            var activeConversations = await _conversationRepository.GetQueryable()
                .Where(x => x.UserId == _currentUser.UserId && x.ProviderId == id && x.Status == "Active")
                .ToListAsync();

            foreach (var conversation in activeConversations)
            {
                conversation.Status = "Archived";
                conversation.UpdateUserId = _currentUser.UserId;
                conversation.UpdateBy = _currentUser.UserName;
                conversation.UpdateTime = DateTime.UtcNow;
                await _conversationRepository.UpdateAsync(conversation);
            }
        }

        // AI provider credentials must not remain in a logically deleted row.
        // Delete this owned provider physically; the repository default is a
        // soft delete, which is not appropriate for secrets or shared accounts.
        return await _db.Deleteable<SysAiProvider>()
            .Where(x => x.Id == id && x.UserId == _currentUser.UserId)
            .ExecuteCommandAsync() > 0;
    }

    public async Task<AiProviderTestResult> TestAsync(long id)
    {
        var entity = await GetOwnedEntityAsync(id);
        if (string.Equals(entity.ExecutionMode, AiExecutionModes.Local, StringComparison.OrdinalIgnoreCase))
        {
            return new AiProviderTestResult
            {
                Success = false,
                Message = "Local providers must be tested by the Tauri LocalAgent."
            };
        }

        // Record where the request is being sent and how it leaves the machine. A TLS failure
        // is almost always a routing problem, so both facts belong in the result.
        var endpoint = AiNetworkRoute.TryResolveEndpoint(entity.BaseUrl);
        var route = AiNetworkRoute.Describe(endpoint);

        try
        {
            var client = _providerFactory.CreateChatClient(entity);
            var toolInvoked = false;
            string CapabilityProbe()
            {
                toolInvoked = true;
                return "CODEMASTER_TOOL_OK";
            }

            var tool = AIFunctionFactory.Create(
                (Func<string>)CapabilityProbe,
                name: "codemaster_capability_probe",
                description: "Return the CodeMaster provider capability probe result.");

            var agent = AiChatClientFactory.CreateAgent(
                client,
                "You are a connectivity probe. Call codemaster_capability_probe exactly once, then reply CODEMASTER_OK.",
                "CodeMasterProviderProbe",
                "Connectivity and tool calling capability probe.",
                [tool],
                entity.CompatibilityMode);

            var runOptions = AiChatClientFactory.CreateRunOptions(
                entity.CompatibilityMode,
                "codemaster_capability_probe");
            var response = await agent.RunAsync(
                "Test the configured model connection and tool calling capability.",
                options: runOptions);
            var responseText = response.ToString();
            entity.SupportsTools = toolInvoked;
            entity.SupportsStreaming = true;
            entity.LastTestAt = DateTime.UtcNow;
            entity.LastTestStatus = "Success";
            entity.LastTestMessage = responseText.Length > 1000 ? responseText[..1000] : responseText;
            entity.UpdateTime = DateTime.UtcNow;
            await _repository.UpdateAsync(entity);

            return new AiProviderTestResult
            {
                Success = true,
                SupportsTools = toolInvoked,
                SupportsStreaming = true,
                Endpoint = endpoint?.ToString(),
                Route = route,
                Message = toolInvoked ? "连接与工具调用均正常。" : "连接成功，但未检测到工具调用能力。",
                ResponseText = responseText
            };
        }
        catch (Exception ex)
        {
            // The outer message alone ("Retry failed after 3 tries.") hides the actual cause.
            // Keep the whole chain plus the route so the failure is actionable.
            var diagnostic = AiRequestFailure.Sanitize(AiRequestFailure.DescribeExceptionChain(ex));
            entity.SupportsTools = false;
            entity.SupportsStreaming = false;
            entity.LastTestAt = DateTime.UtcNow;
            entity.LastTestStatus = "Failed";
            entity.LastTestMessage = diagnostic.Length > 1000 ? diagnostic[..1000] : diagnostic;
            entity.UpdateTime = DateTime.UtcNow;
            await _repository.UpdateAsync(entity);

            return new AiProviderTestResult
            {
                Success = false,
                Endpoint = endpoint?.ToString(),
                Route = route,
                Diagnostic = diagnostic,
                Message = $"{diagnostic}{Environment.NewLine}{route}"
            };
        }
    }

    private async Task<SysAiProvider> GetOwnedEntityAsync(long id)
    {
        var userId = _currentUser.UserId;
        var entity = await _repository.GetQueryable()
            .Where(x => x.Id == id && x.UserId == userId)
            .FirstAsync();

        return entity ?? throw new KeyNotFoundException("AI provider was not found.");
    }

    private async Task ClearOtherDefaultsAsync(long currentId)
    {
        var userId = _currentUser.UserId;
        await _db.Updateable<SysAiProvider>()
            .SetColumns(x => x.IsDefault == false)
            .Where(x => x.UserId == userId && x.Id != currentId && x.IsDefault)
            .ExecuteCommandAsync();
    }

    private static void Validate(SaveAiProviderRequest input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new ArgumentException("Provider name is required.");
        }

        if (string.IsNullOrWhiteSpace(input.ModelName))
        {
            throw new ArgumentException("Model name is required.");
        }

        if (input.ProviderType is not (AiProviderTypes.OpenAiCompatible or AiProviderTypes.Anthropic))
        {
            throw new ArgumentException("Unsupported provider type.");
        }

        if (input.ExecutionMode is not (AiExecutionModes.Server or AiExecutionModes.Local))
        {
            throw new ArgumentException("Unsupported execution mode.");
        }

        if (!AiProviderCompatibilityModes.IsSupported(input.CompatibilityMode))
        {
            throw new ArgumentException("Unsupported AI provider compatibility mode.");
        }

        if (string.Equals(input.CompatibilityMode, AiProviderCompatibilityModes.OpenRouterReasoning, StringComparison.OrdinalIgnoreCase) &&
            input.ProviderType != AiProviderTypes.OpenAiCompatible)
        {
            throw new ArgumentException("OpenRouter Reasoning compatibility requires an OpenAI-compatible provider.");
        }

        if (!string.IsNullOrWhiteSpace(input.BaseUrl) &&
            (!Uri.TryCreate(input.BaseUrl, UriKind.Absolute, out var parsedUri) ||
             parsedUri is not { Scheme: "http" or "https" }))
        {
            throw new ArgumentException("Base URL must be an absolute http/https URL.");
        }

        if (input.ProviderType == AiProviderTypes.OpenAiCompatible)
        {
            _ = OpenAiCompatibleHttpHandler.ParseExtraHeaders(input.ExtraHeadersJson);
        }
    }

    private static string? NormalizeBaseUrl(string providerType, string? baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            return baseUrl.Trim().TrimEnd('/');
        }

        return providerType == AiProviderTypes.Anthropic
            ? "https://api.anthropic.com"
            : "https://api.openai.com/v1";
    }

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeCompatibilityMode(string? value) =>
        string.Equals(value, AiProviderCompatibilityModes.OpenRouterReasoning, StringComparison.OrdinalIgnoreCase)
            ? AiProviderCompatibilityModes.OpenRouterReasoning
            : AiProviderCompatibilityModes.Standard;

    private static bool ShouldPersistServerSecret(SaveAiProviderRequest input) =>
        input.ExecutionMode == AiExecutionModes.Server && !string.IsNullOrWhiteSpace(input.ApiKey);

    private static bool ShouldPersistServerSettings(SaveAiProviderRequest input) =>
        input.ExecutionMode == AiExecutionModes.Server;

    private static AiProviderDto ToDto(SysAiProvider entity)
    {
        return new AiProviderDto
        {
            Id = entity.Id,
            Name = entity.Name,
            ProviderType = entity.ProviderType,
            ExecutionMode = entity.ExecutionMode,
            CompatibilityMode = NormalizeCompatibilityMode(entity.CompatibilityMode),
            BaseUrl = entity.BaseUrl,
            ModelName = entity.ModelName,
            ExtraHeadersJson = string.Equals(entity.ExecutionMode, AiExecutionModes.Local, StringComparison.OrdinalIgnoreCase)
                ? null
                : entity.ExtraHeadersJson,
            HasApiKey = !string.IsNullOrWhiteSpace(entity.ApiKeyCipherText),
            IsDefault = entity.IsDefault,
            IsEnabled = entity.IsEnabled,
            SupportsTools = entity.SupportsTools,
            SupportsStreaming = entity.SupportsStreaming,
            LastTestAt = entity.LastTestAt,
            LastTestStatus = entity.LastTestStatus,
            LastTestMessage = entity.LastTestMessage
        };
    }
}
