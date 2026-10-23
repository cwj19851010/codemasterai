using System.Text.Json;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Application.Services.CodeGen;
using CodeMaster.Core.Entities;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.LocalAgent.Extensibility;
using CodeMaster.LocalAgent.Models;
using CodeMaster.UiComposition.Entities;
using SqlSugar;

namespace CodeMaster.UiComposition.LocalAgent;

public sealed class UiCompositionLocalMetadataContributor : ILocalMetadataBundleContributor
{
    private static readonly Type[] Types =
    [
        typeof(ProjectPage),
        typeof(PageDataSource),
        typeof(PageComponentBinding),
        typeof(PageEntityDependency),
        typeof(PageLayoutPreset),
        typeof(PageDisplayTemplate),
        typeof(ProjectDataSource),
        typeof(PageDataSourceBinding)
    ];

    public IReadOnlyCollection<Type> EntityTypes => Types;

    public async Task RestoreAsync(GenerationBundleDto bundle, ISqlSugarClient db)
    {
        if (!bundle.Extensions.TryGetValue(ProjectPageModuleContract.Key, out var payload))
            return;

        var extension = payload.Deserialize<ProjectPageBundleExtension>()
            ?? throw new InvalidOperationException("UI composition generation metadata is invalid.");
        Normalize(extension.Pages);
        Normalize(extension.DataSources);
        Normalize(extension.ComponentBindings);
        Normalize(extension.EntityDependencies);
        Normalize(extension.LayoutPresets);
        Normalize(extension.DisplayTemplates);
        Normalize(extension.ProjectDataSources);
        Normalize(extension.DataSourceBindings);

        await InsertAsync(db, extension.Pages);
        await InsertAsync(db, extension.DataSources);
        await InsertAsync(db, extension.ComponentBindings);
        await InsertAsync(db, extension.EntityDependencies);
        await InsertAsync(db, extension.LayoutPresets);
        await InsertAsync(db, extension.DisplayTemplates);
        await InsertAsync(db, extension.ProjectDataSources);
        await InsertAsync(db, extension.DataSourceBindings);
    }

    private static async Task InsertAsync<T>(ISqlSugarClient db, List<T> rows) where T : class, new()
    {
        if (rows.Count > 0)
            await db.Insertable(rows).ExecuteCommandAsync();
    }

    private static void Normalize<T>(IEnumerable<T> rows) where T : EntityBase
    {
        foreach (var row in rows)
        {
            row.CreateBy ??= "LocalAgent";
            row.UpdateUserId ??= 0;
            if (row.CreateTime == default)
                row.CreateTime = DateTime.UtcNow;
            foreach (var property in row.GetType().GetProperties())
            {
                if (property.PropertyType == typeof(string) && property.CanRead && property.CanWrite && property.GetValue(row) == null)
                    property.SetValue(row, string.Empty);
            }
        }
    }
}

public sealed class UiCompositionLocalCodegenActionHandler : ILocalCodegenActionHandler
{
    public bool CanHandle(string action) =>
        string.Equals(action, "generateComplexPage", StringComparison.OrdinalIgnoreCase);

    public bool ReportCompletion => true;

    public async Task<LocalExecutionResult> ExecuteAsync(LocalCodegenActionContext context)
    {
        var request = context.Request;
        var pageId = GetRequiredLong(request.Payload, "pageId", "id");
        var projectId = GetRequiredLong(request.Payload, "projectId");
        var bundle = await context.ServerClient.GetGenerationBundleAsync(
            request.ServerBaseUrl,
            projectId,
            request.AccessToken);
        ValidateBundle(bundle);

        var extension = bundle.Extensions[ProjectPageModuleContract.Key]
            .Deserialize<ProjectPageBundleExtension>()
            ?? throw new InvalidOperationException("UI composition generation metadata is invalid.");
        if (!extension.Pages.Any(page => page.Id == pageId && page.ProjectId == projectId && !page.IsDeleted))
            throw new InvalidOperationException($"Project page {pageId} was not included in the generation bundle.");

        var overridePath = GetString(request.Payload, "projectPath") ?? GetString(request.Payload, "targetPath");
        if (!string.IsNullOrWhiteSpace(overridePath))
            bundle.Project.ProjectPath = ResolveProjectPath(bundle.Project.ProjectName, overridePath);

        using var metadata = await context.MetadataStore.CreateAsync(bundle, bundle.Project.ProjectPath);
        var pageService = new ProjectPageService(metadata.Db);
        var generationService = new ComplexPageGenerationService(metadata.Db, pageService);
        var result = await generationService.GenerateAsync(
            pageId,
            GetBoolean(request.Payload, "incremental", defaultValue: true));
        return result.Success
            ? LocalExecutionResult.OkData(result)
            : LocalExecutionResult.Fail(result.Error ?? "Complex page generation failed");
    }

    public void ConfigureCompletion(
        LocalExecutionCompleteDto completion,
        LocalExecutionRequest request,
        LocalExecutionResult result)
    {
        completion.Extensions[ProjectPageModuleContract.Key] = JsonSerializer.SerializeToElement(
            new ProjectPageExecutionCompletion
            {
                PageId = GetRequiredLong(request.Payload, "pageId", "id"),
                LastGeneratedVueHash = (result.Data as ComplexPageGenerationResultDto)?.VueHash
            });
    }

    public static void ValidateBundle(GenerationBundleDto bundle)
    {
        if (!bundle.ExtensionVersions.TryGetValue(ProjectPageModuleContract.Key, out var version))
            throw new InvalidOperationException("The generation bundle does not contain UI composition metadata.");
        if (version < ProjectPageModuleContract.Version)
            throw new InvalidOperationException($"UI composition metadata version {version} is too old.");
        if (version > ProjectPageModuleContract.Version)
            throw new InvalidOperationException($"Unsupported UI composition metadata version {version}.");
    }

    private static long GetRequiredLong(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(payload, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
                return number;
        }

        throw new InvalidOperationException($"Required value is missing: {string.Join('/', names)}");
    }

    private static string? GetString(JsonElement payload, string name)
    {
        return TryGetProperty(payload, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool GetBoolean(JsonElement payload, string name, bool defaultValue)
    {
        if (!TryGetProperty(payload, name, out var value))
            return defaultValue;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => defaultValue
        };
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string ResolveProjectPath(string projectName, string path)
    {
        if (!Path.IsPathRooted(path))
            throw new InvalidOperationException("Project path must be absolute.");
        var fullPath = Path.GetFullPath(path);
        return fullPath.EndsWith(projectName, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.Combine(fullPath, projectName);
    }
}
