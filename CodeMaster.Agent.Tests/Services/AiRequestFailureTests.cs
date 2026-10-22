using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;
using CodeMaster.Agent.Tools;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Domain.Entities.Ai;
using CodeMaster.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.AI;
using SqlSugar;

namespace CodeMaster.Agent.Tests.Services;

public sealed class AiRequestFailureTests : IDisposable
{
    private readonly SqlSugarClient _db;
    private readonly long _conversationId = DateTime.UtcNow.Ticks;
    private readonly AiConversationService _service;
    private int _modelCalls;

    public AiRequestFailureTests()
    {
        _db = new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = "Data Source=:memory:", DbType = DbType.Sqlite,
            IsAutoCloseConnection = false, InitKeyType = InitKeyType.Attribute
        });
        _db.Ado.Open();
        _db.CodeFirst.InitTables<AiConversation, AiMessage, SysAiProvider, AiToolExecution>();
        _db.Insertable(new AiConversation { Id = _conversationId, UserId = 1, ProjectId = 9, ProviderId = 2, CreateBy = "test", UpdateUserId = 0 }).ExecuteCommand();
        _db.Insertable(new SysAiProvider { Id = 2, UserId = 1, ModelName = "test", Name = "test", CreateBy = "test", UpdateUserId = 0 }).ExecuteCommand();
        _service = new AiConversationService(
            new Repository<AiConversation>(_db), new MessageRepository(_db), new Repository<AiToolExecution>(_db),
            new Repository<SysAiProvider>(_db), new User(),
            Stub<IAiProviderFactory>((_, _) =>
            {
                _modelCalls++;
                if (_modelCalls <= 2) throw new TimeoutException("模型连接超时; api_key=secret-value");
                return new AnswerClient();
            }),
            Stub<ICodeMasterAgentToolFactory>((_, _) => Task.FromResult<IList<AITool>>([])),
            Stub<IAiProjectWorkPlanService>(),
            Stub<IProjectService>((method, _) => method.Name == "GetByIdAsync"
                ? Task.FromResult(new ProjectDto { Id = 9, ProjectName = "Test", DisplayName = "测试" })
                : throw new InvalidOperationException(method.Name)),
            Stub<IAiAuthorizationService>((_, _) => Task.CompletedTask),
            Stub<ICodeMasterProjectBlueprintService>((method, _) => throw new InvalidOperationException(method.Name)));
    }

    [Fact]
    public async Task Failure_is_persisted_deduplicated_and_retry_reaches_model_then_resolves()
    {
        var input = new SendAiMessageRequest { ConversationId = _conversationId, RequestId = "retry-request", Content = "你好" };
        await Assert.ThrowsAsync<TimeoutException>(() => _service.SendAsync(input));
        await Assert.ThrowsAsync<TimeoutException>(() => _service.SendAsync(input));
        var messages = await _service.GetMessagesAsync(_conversationId);
        Assert.Single(messages, message => message.Role == "user");
        var failure = Assert.Single(messages, message => message.Role == "error");
        Assert.DoesNotContain("secret-value", failure.Content);
        Assert.Contains("超时", failure.Content);
        Assert.DoesNotContain(AiConversationHistory.BuildBounded(messages), message => message.Text.Contains("超时"));
        Assert.DoesNotContain(messages, message => message.Role == "assistant");

        var result = await _service.SendAsync(input);
        Assert.Equal("处理完成", result.Message.Content);
        Assert.Equal(3, _modelCalls);
        var retried = await _service.SendAsync(input);
        Assert.Equal(result.Message.Id, retried.Message.Id);
        Assert.Equal(3, _modelCalls);
        messages = await _service.GetMessagesAsync(_conversationId);
        Assert.Single(messages, message => message.Role == "assistant");
        failure = Assert.Single(messages, message => message.Role == "error");
        Assert.Equal("Resolved", JsonDocument.Parse(failure.MetadataJson!).RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Unowned_conversation_cannot_create_error_messages()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.SendAsync(new()
        { ConversationId = -1, RequestId = "unowned", Content = "你好" }));
        Assert.Empty(await _db.Queryable<AiMessage>().ToListAsync());
    }

    [Fact]
    public async Task Local_failure_requires_started_owned_request_and_does_not_mark_it_completed()
    {
        await _db.Updateable<SysAiProvider>().SetColumns(provider => provider.ExecutionMode == "Local").Where(provider => provider.Id == 2).ExecuteCommandAsync();
        var begin = await _service.BeginLocalAsync(new() { ConversationId = _conversationId, RequestId = "local-fail", Content = "你好" });
        Assert.False(begin.AlreadyCompleted);
        await _service.FailLocalAsync(new() { ConversationId = _conversationId, RequestId = "local-fail", ErrorMessage = "Connection failed: Bearer secret-token" });
        begin = await _service.BeginLocalAsync(new() { ConversationId = _conversationId, RequestId = "local-fail", Content = "你好" });
        Assert.False(begin.AlreadyCompleted);
        var failure = Assert.Single(begin.Messages, message => message.Role == "error");
        Assert.DoesNotContain("secret-token", failure.Content);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.FailLocalAsync(new()
        { ConversationId = _conversationId, RequestId = "never-started", ErrorMessage = "bad" }));
    }

    [Fact]
    public void Diagnostics_redact_secrets_and_are_bounded()
    {
        var text = AiRequestFailure.Sanitize("Bearer abc.def; Password=hidden; api_key=secret; https://user:pass@example.test/path");
        Assert.DoesNotContain("abc.def", text);
        Assert.DoesNotContain("hidden", text);
        Assert.DoesNotContain("=secret", text);
        Assert.DoesNotContain("user:pass", text);
        Assert.True(AiRequestFailure.Sanitize(new string('x', 9000)).Length <= 1801);
    }

    private sealed class MessageRepository(ISqlSugarClient db) : Repository<AiMessage>(db)
    {
        private static long _id = 1000000;
        public override async Task<long> InsertAsync(AiMessage entity)
        {
            entity.Id = Interlocked.Increment(ref _id);
            entity.UpdateUserId ??= 0;
            await _db.Insertable(entity).ExecuteCommandAsync();
            return entity.Id;
        }
    }

    private sealed class User : IAiCurrentUser
    {
        public long UserId => 1;
        public long TenantId => 0;
        public string UserName => "test";
        public ClaimsPrincipal Principal => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")], "test"));
    }

    private sealed class AnswerClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "处理完成")) { FinishReason = ChatFinishReason.Stop });
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type type, object? key = null) => type == typeof(ChatClientMetadata) ? new ChatClientMetadata("test") : null;
        public void Dispose() { }
    }

    private static T Stub<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class
    {
        var instance = DispatchProxy.Create<T, ServiceProxy>();
        ((ServiceProxy)(object)instance).Handler = handler;
        return instance;
    }

    public class ServiceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            Handler?.Invoke(method!, args) ?? throw new InvalidOperationException($"Unexpected call: {method?.Name}");
    }

    public void Dispose() => _db.Dispose();
}
