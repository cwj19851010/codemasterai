using System.Text.Json;

namespace CodeMaster.Application.Dtos.CodeGen;

/// <summary>
/// 客户端本地执行完成后回写服务器元数据状态。
/// </summary>
public class LocalExecutionCompleteDto
{
    public string Action { get; set; } = string.Empty;

    public long? ProjectId { get; set; }

    public long? EntityId { get; set; }

    public long? ModuleId { get; set; }

    public List<long> EntityIds { get; set; } = new();

    public Dictionary<string, JsonElement> Extensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}
