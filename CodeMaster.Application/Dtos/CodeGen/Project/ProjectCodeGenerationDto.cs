using System.Text.Json.Serialization;

namespace CodeMaster.Application.Dtos.CodeGen;

/// <summary>
/// Ids are snowflakes, so the Agent runtime serialises them as strings to keep JavaScript from
/// losing precision. Accept both spellings here: strict number-only binding silently produced a
/// null payload, which surfaced much later as a NullReferenceException inside generation.
/// </summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public class ProjectCodeGenerationDto
{
    public long ProjectId { get; set; }

    public List<long> EntityIds { get; set; } = new();
}
