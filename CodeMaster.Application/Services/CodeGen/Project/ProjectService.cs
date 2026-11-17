using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Core.DynamicApi;
using CodeMaster.Core.Dtos;
using CodeMaster.Core.Repositories;
using CodeMaster.Core.Services;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.Domain.Entities.System;
using CodeMaster.Infrastructure.Persistence.SqlSugar;
using Mapster;
using SqlSugar;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Yitter.IdGenerator;

namespace CodeMaster.Application.Services.CodeGen;

/// <summary>
/// 项目管理服务
/// </summary>
public class ProjectService : CrudApplicationService<Project, ProjectDto, ProjectDto, ProjectQueryDto, CreateProjectDto, UpdateProjectDto>, IProjectService
{
    private readonly TemplateExportService _templateExportService;
    private readonly ProjectInitializationService _initializationService;
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISqlSugarClient _db;

    public ProjectService(
        IRepository<Project> repository,
        IExcelService excelService,
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        ISqlSugarClient db,
        CodeMaster.Core.Services.ICacheService? cacheService = null) : base(repository, excelService, cacheService)
    {
        _configuration = configuration;
        _serviceProvider = serviceProvider;
        _db = db;
        _templateExportService = new TemplateExportService();
        _initializationService = new ProjectInitializationService(
            configuration,
            serviceProvider.GetServices<IGeneratedProjectInitializer>());
    }

    /// <summary>
    /// 创建项目：SQLite 连接串只保留数据库文件名（路径与 Data Source 前缀由平台统一处理）
    /// </summary>
    public override async Task<long> CreateAsync(CreateProjectDto input)
    {
        input.ConnectionString = NormalizeSqliteConnectionString(input.DatabaseType, input.ConnectionString);
        return await base.CreateAsync(input);
    }

    /// <summary>
    /// 更新项目：SQLite 连接串只保留数据库文件名
    /// </summary>
    public override async Task<int> UpdateAsync(long id, UpdateProjectDto input)
    {
        input.ConnectionString = NormalizeSqliteConnectionString(input.DatabaseType, input.ConnectionString);
        return await base.UpdateAsync(id, input);
    }

    /// <summary>
    /// 规范化 SQLite 连接串：剥掉重复的 Data Source 前缀和路径，只保留数据库文件名
    /// </summary>
    internal static string NormalizeSqliteConnectionString(Core.Enums.DatabaseType databaseType, string connectionString)
    {
        if (databaseType != Core.Enums.DatabaseType.SQLite || string.IsNullOrWhiteSpace(connectionString))
            return connectionString;

        if (!Regex.IsMatch(connectionString, @"Data Source\s*=", RegexOptions.IgnoreCase))
            return connectionString;

        var value = ExtractSqliteFileName(connectionString);
        return string.IsNullOrWhiteSpace(value) ? connectionString : $"Data Source={value}";
    }

    /// <summary>
    /// SQLite 只创建数据库文件，不创建它的上级目录：连接串指向一个不存在的目录时，
    /// 打开连接会失败并报 "SQLite Error 14: unable to open database file"。
    /// 非向导创建的项目（例如 Agent 直接建的项目）此时还没有 {ProjectName}.Migrator 目录，
    /// 所以在使用连接串之前按需把目录建出来。
    /// </summary>
    public static string EnsureSqliteDirectory(string absolutePath)
    {
        var directory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        return absolutePath;
    }

    /// <summary>
    /// 从 SQLite 连接串中提取数据库文件名（剥掉重复 Data Source 前缀、路径、引号）
    /// </summary>
    internal static string ExtractSqliteFileName(string connectionString)
    {
        var value = ExtractSqliteDataSource(connectionString);

        // 只保留文件名（剥掉路径）
        if (value.Contains('\\') || value.Contains('/'))
        {
            value = Path.GetFileName(value.Trim().Trim('"'));
        }

        return value.Trim();
    }

    internal static string ExtractSqliteDataSource(string connectionString)
    {
        var match = Regex.Match(connectionString, @"Data Source\s*=\s*([^;]+)", RegexOptions.IgnoreCase);
        if (!match.Success)
            return string.Empty;

        var value = match.Groups[1].Value.Trim().Trim('"');
        while (Regex.IsMatch(value, @"^Data Source\s*=", RegexOptions.IgnoreCase))
            value = Regex.Replace(value, @"^Data Source\s*=", "", RegexOptions.IgnoreCase).Trim().Trim('"');
        return value.Trim();
    }

    /// <summary>
    /// 导出模板
    /// </summary>
    public async Task<string> ExportTemplateAsync(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            outputPath = GetWritableTemplatesDirectory();

        return await _templateExportService.ExportCleanTemplateAsync(outputPath);
    }

    /// <summary>
    /// 获取模板 Base64（用于客户端）
    /// </summary>
    public async Task<string> GetTemplateBase64Async()
    {
        // 1. 获取最新的模板文件
        var templateZipPath = GetTemplateZipPath();

        // 2. 读取文件并转换为 Base64
        var bytes = await File.ReadAllBytesAsync(templateZipPath);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// 客户端初始化项目（用于客户端 WebView）
    /// </summary>
    public async Task<ClientInitializeProjectDto> GetClientInitializeDataAsync(long id)
    {
        // 1. 查询项目记录
        var project = await Repository.GetByIdAsync(id);
        if (project == null)
            throw new Exception("Project not found");

        // 2. 获取模板 Base64
        var templateBase64 = await GetTemplateBase64Async();

        // 3. 返回客户端初始化所需的数据
        return new ClientInitializeProjectDto
        {
            Id = project.Id,
            TemplateBase64 = templateBase64,
            ProjectName = project.ProjectName,
            ProjectPath = project.ProjectPath,
            DatabaseType = project.DatabaseType.ToString(),
            ConnectionString = project.ConnectionString
        };
    }

    /// <summary>
    /// Get the latest project template ZIP file for download.
    /// </summary>
    [DynamicApi(IsEnabled = false)]
    public async Task<ProjectTemplateFileDto> GetTemplateFileAsync()
    {
        var templateZipPath = GetTemplateZipPath();
        var bytes = await File.ReadAllBytesAsync(templateZipPath);

        return new ProjectTemplateFileDto
        {
            FileName = Path.GetFileName(templateZipPath),
            Content = bytes
        };
    }

    /// <summary>
    /// Save a new project template ZIP file for later initialization/download.
    /// </summary>
    [DynamicApi(IsEnabled = false)]
    public async Task<ProjectTemplateUploadResultDto> SaveTemplateFileAsync(Stream stream, string fileName, long length)
    {
        const long maxTemplateSize = 500L * 1024 * 1024;

        if (stream == null || length <= 0)
            throw new Exception("Template file cannot be empty");

        if (length > maxTemplateSize)
            throw new Exception("Template file cannot exceed 500 MB");

        if (!string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Template file must be a .zip archive");

        var templatesDir = GetWritableTemplatesDirectory();
        Directory.CreateDirectory(templatesDir);

        var savedAt = DateTime.UtcNow;
        var savedFileName = $"CodeMaster_Template_{savedAt:yyyyMMddHHmmssfff}.zip";
        var savedPath = Path.Combine(templatesDir, savedFileName);

        await using (var fileStream = new FileStream(savedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await stream.CopyToAsync(fileStream);
        }

        try
        {
            ValidateProjectTemplateZip(savedPath);
        }
        catch
        {
            File.Delete(savedPath);
            throw;
        }

        File.SetLastWriteTimeUtc(savedPath, savedAt);

        return new ProjectTemplateUploadResultDto
        {
            FileName = savedFileName,
            FileSize = new FileInfo(savedPath).Length,
            UploadedAt = savedAt
        };
    }

    /// <summary>
    /// 获取客户端本地执行代码生成所需的完整上下文快照。
    /// </summary>
    public async Task<GenerationBundleDto> GetGenerationBundleAsync(long id)
    {
        var project = await Repository.GetByIdAsync(id);
        if (project == null)
            throw new Exception("Project not found");

        var modules = await Repository.GetQueryable<ProjectModule>()
            .Where(m => m.ProjectId == id && m.IsDeleted == false)
            .OrderBy(m => m.OrderNum)
            .ToListAsync();

        var entities = await Repository.GetQueryable<ModuleEntity>()
            .Where(e => e.ProjectId == id && e.IsDeleted == false)
            .OrderBy(e => e.OrderNum)
            .ToListAsync();

        var entityIds = entities.Select(e => e.Id).ToList();
        var fields = new List<EntityField>();
        var relations = new List<OneToManyRelation>();
        var entityRelations = new List<EntityRelation>();

        if (entityIds.Count > 0)
        {
            fields = await Repository.GetQueryable<EntityField>()
                .Where(f => entityIds.Contains(f.ModuleEntityId) && f.IsDeleted == false)
                .OrderBy(f => f.OrderNum)
                .ToListAsync();

            relations = await Repository.GetQueryable<OneToManyRelation>()
                .Where(r => (entityIds.Contains(r.ModuleEntityId) || entityIds.Contains(r.ChildEntityId)) && r.IsDeleted == false)
                .OrderBy(r => r.OrderNum)
                .ToListAsync();

            if (_db.DbMaintenance.IsAnyTable("sys_entity_relation", false))
            {
                entityRelations = await Repository.GetQueryable<EntityRelation>()
                    .Where(r => (entityIds.Contains(r.SourceEntityId) || entityIds.Contains(r.TargetEntityId)) && !r.IsDeleted)
                    .OrderBy(r => r.OrderNum)
                    .ToListAsync();
            }
        }

        var bundle = new GenerationBundleDto
        {
            Project = project,
            Modules = modules,
            Entities = entities,
            Fields = fields,
            Relations = relations,
            EntityRelations = entityRelations,
            PageTemplates = await Repository.GetQueryable<SysPageTemplate>()
                .Where(t => t.IsDeleted == false)
                .OrderBy(t => t.PageType)
                .OrderBy(t => t.Sort)
                .ToListAsync(),
            FieldControlTemplates = await Repository.GetQueryable<SysFieldControlTemplate>()
                .Where(t => t.IsDeleted == false)
                .OrderBy(t => t.ControlType)
                .OrderBy(t => t.PageSection)
                .OrderBy(t => t.Sort)
                .ToListAsync(),
            ChildTemplates = await Repository.GetQueryable<SysChildTemplate>()
                .Where(t => t.IsDeleted == false)
                .OrderBy(t => t.PageType)
                .OrderBy(t => t.ChildType)
                .OrderBy(t => t.Sort)
                .ToListAsync()
        };

        foreach (var contributor in _serviceProvider.GetServices<IGenerationBundleContributor>())
            await contributor.ContributeAsync(id, bundle);

        return bundle;
    }

    /// <summary>
    /// 客户端本地执行完成后回写服务器元数据状态。
    /// </summary>
    public async Task<bool> CompleteLocalExecutionAsync(LocalExecutionCompleteDto input)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));

        var action = input.Action?.Trim();
        if (string.IsNullOrWhiteSpace(action))
            throw new ArgumentException("Action is required", nameof(input));

        var completedAt = input.CompletedAt == default
            ? DateTime.UtcNow
            : input.CompletedAt.Kind == DateTimeKind.Utc
                ? input.CompletedAt
                : input.CompletedAt.ToUniversalTime();

        switch (action)
        {
            case "initializeProject":
            case "initializeStep11":
                await MarkProjectStatusAsync(input.ProjectId, Core.Enums.ProjectStatus.Initialized, completedAt);
                return true;

            case "startProject":
                await MarkProjectStatusAsync(input.ProjectId, Core.Enums.ProjectStatus.Running, completedAt);
                return true;

            case "stopProject":
                await MarkProjectStatusAsync(input.ProjectId, Core.Enums.ProjectStatus.Stopped, completedAt);
                return true;

            case "generateCode":
            case "generateIncrementalCode":
            case "generateSmartCode":
            case "generateProjectCode":
            case "generateProjectIncrementalCode":
            case "generateProjectSmartCode":
                await MarkGeneratedEntitiesAsync(input, completedAt);
                return true;

            case "syncModuleToMenu":
                await MarkModuleSyncedAsync(input.ModuleId, completedAt);
                return true;

            default:
                foreach (var handler in _serviceProvider.GetServices<ILocalExecutionCompletionHandler>())
                {
                    if (handler.CanHandle(action))
                        await handler.HandleAsync(input, completedAt);
                }
                return true;
        }
    }

    private async Task MarkProjectStatusAsync(long? projectId, Core.Enums.ProjectStatus status, DateTime completedAt)
    {
        if (!projectId.HasValue || projectId.Value == 0)
            throw new ArgumentException("ProjectId is required");

        var project = await Repository.GetByIdAsync(projectId.Value);
        if (project == null)
            throw new Exception("Project not found");

        project.Status = status;
        if (status == Core.Enums.ProjectStatus.Initialized)
        {
            project.InitializedAt = completedAt;
            project.InitError = null;
        }

        await Repository.UpdateAsync(project);
    }

    private async Task MarkGeneratedEntitiesAsync(LocalExecutionCompleteDto input, DateTime completedAt)
    {
        var entityIds = await ResolveGeneratedEntityIdsAsync(input);
        if (entityIds.Count == 0)
            throw new ArgumentException("EntityId is required");

        var entityIdList = entityIds.ToList();
        var entities = await _db.Queryable<ModuleEntity>()
            .Where(e => entityIdList.Contains(e.Id) && e.IsDeleted == false)
            .ToListAsync();

        foreach (var entity in entities)
        {
            entity.IsGenerated = true;
            entity.LastGeneratedTime = completedAt;
            await _db.Updateable(entity).ExecuteCommandAsync();
        }
    }

    private async Task<HashSet<long>> ResolveGeneratedEntityIdsAsync(LocalExecutionCompleteDto input)
    {
        var result = input.EntityIds.Where(id => id > 0).ToHashSet();
        if (input.EntityId.HasValue && input.EntityId.Value > 0)
            result.Add(input.EntityId.Value);

        if (result.Count == 0 &&
            input.ProjectId.HasValue &&
            input.ProjectId.Value > 0 &&
            input.Action is "generateProjectCode" or "generateProjectIncrementalCode" or "generateProjectSmartCode")
        {
            return (await _db.Queryable<ModuleEntity>()
                    .Where(e => e.ProjectId == input.ProjectId.Value && e.IsDeleted == false)
                    .Select(e => e.Id)
                    .ToListAsync())
                .ToHashSet();
        }

        if (result.Count == 0)
            return result;

        var knownEntities = await _db.Queryable<ModuleEntity>()
            .Where(e => result.Contains(e.Id) && e.IsDeleted == false)
            .ToListAsync();

        if (knownEntities.Count == 0)
            throw new Exception("Entity not found");

        if (input.ProjectId.HasValue && input.ProjectId.Value > 0 &&
            knownEntities.Any(e => e.ProjectId != input.ProjectId.Value))
        {
            throw new Exception("Entity does not belong to the project");
        }

        var projectId = input.ProjectId.GetValueOrDefault(knownEntities.First().ProjectId);
        var projectEntityIds = await _db.Queryable<ModuleEntity>()
            .Where(e => e.ProjectId == projectId && e.IsDeleted == false)
            .Select(e => e.Id)
            .ToListAsync();

        var relations = await _db.Queryable<OneToManyRelation>()
            .Where(r => r.IsDeleted == false &&
                (projectEntityIds.Contains(r.ModuleEntityId) || projectEntityIds.Contains(r.ChildEntityId)))
            .ToListAsync();

        var queue = new Queue<long>(result);
        while (queue.Count > 0)
        {
            var parentId = queue.Dequeue();
            foreach (var childId in relations.Where(r => r.ModuleEntityId == parentId).Select(r => r.ChildEntityId))
            {
                if (childId <= 0 || !result.Add(childId))
                    continue;

                queue.Enqueue(childId);
            }
        }

        return result;
    }

    private async Task MarkModuleSyncedAsync(long? moduleId, DateTime completedAt)
    {
        if (!moduleId.HasValue || moduleId.Value == 0)
            throw new ArgumentException("ModuleId is required");

        var module = await _db.Queryable<ProjectModule>()
            .Where(m => m.Id == moduleId.Value && m.IsDeleted == false)
            .FirstAsync();

        if (module == null)
            throw new Exception("Module not found");

        module.IsSynced = true;
        module.LastSyncTime = completedAt;
        await _db.Updateable(module).ExecuteCommandAsync();
    }

    /// <summary>
    /// 创建查询
    /// </summary>
    protected override async Task<ISugarQueryable<Project>> CreateFilteredQueryAsync(ProjectQueryDto? input)
    {
        var query = await base.CreateFilteredQueryAsync(input);

        if (input == null)
            return query;

        if (!string.IsNullOrWhiteSpace(input.ProjectName))
            query = query.Where(x => x.ProjectName.Contains(input.ProjectName));

        if (!string.IsNullOrWhiteSpace(input.DisplayName))
            query = query.Where(x => x.DisplayName.Contains(input.DisplayName));

        if (input.DatabaseType.HasValue)
            query = query.Where(x => x.DatabaseType == input.DatabaseType.Value);

        if (input.Status.HasValue)
            query = query.Where(x => x.Status == input.Status.Value);

        return query;
    }

    /// <summary>
    /// 初始化项目
    /// </summary>
    [DynamicApi(IsEnabled = false)]
    public async Task<bool> InitializeAsync(InitializeProjectDto input)
    {
        Console.WriteLine($"[InitializeAsync] Received input: Id={input?.Id}, TargetPath={input?.TargetPath}");

        if (input == null)
            throw new ArgumentNullException(nameof(input), "Input cannot be null");

        if (input.Id == 0)
            throw new ArgumentException("Project ID cannot be 0", nameof(input.Id));

        // 直接查询，避免 Repository 的过滤器
        var project = await Repository.GetByIdAsync(input.Id);
        if (project == null)
            throw new Exception("Project not found");

        //if (project.Status == Core.Enums.ProjectStatus.Initialized)
        //    throw new Exception("Project already initialized");

        try
        {
            // 设置进度回调
            _initializationService.SetProgressCallback((projectId, step, message, progress) =>
            {
                try
                {
                    // 使用反射获取 IHubContext（避免直接引用 Microsoft.AspNetCore.SignalR）
                    var hubContextType = Type.GetType("Microsoft.AspNetCore.SignalR.IHubContext`1, Microsoft.AspNetCore.SignalR");
                    if (hubContextType != null)
                    {
                        var hubType = Type.GetType("CodeMaster.WebApi.Hubs.ProjectInitializationHub, CodeMaster.WebApi");
                        if (hubType != null)
                        {
                            var genericHubContextType = hubContextType.MakeGenericType(hubType);
                            var hubContext = _serviceProvider.GetService(genericHubContextType);

                            if (hubContext != null)
                            {
                                var clientsProperty = genericHubContextType.GetProperty("Clients");
                                var clients = clientsProperty?.GetValue(hubContext);

                                if (clients != null)
                                {
                                    var allProperty = clients.GetType().GetProperty("All");
                                    var allClients = allProperty?.GetValue(clients);

                                    if (allClients != null)
                                    {
                                        var sendAsyncMethod = allClients.GetType().GetMethod("SendAsync");
                                        sendAsyncMethod?.Invoke(allClients, new object[] { "ReceiveProgress", new object[] { projectId, step, message, progress }, default(CancellationToken) });
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SignalR Error] {ex.Message}");
                }
            });

            // 1. 获取最新的模板文件
        var templateZipPath = GetTemplateZipPath();

            // 使用传入的 TargetPath 或项目记录中的 ProjectPath（同时校验必须是绝对路径）
            var fullProjectPath = GetFullProjectPath(project, input.TargetPath);

            // 2. 初始化项目
            await _initializationService.InitializeProjectAsync(
                project.ProjectName,
                fullProjectPath,
                project.DatabaseType.ToString(),
                project.ConnectionString,
                templateZipPath,
                project.FrontendPort ?? 0,
                project.BackendPort ?? 0,
                project.Id.ToString(),
                project.DisplayName);

            // 注意：数据库迁移、npm install、服务启动已在 InitializeProjectAsync 中完成
            // 不需要再单独调用

            project.Status = Core.Enums.ProjectStatus.Initialized;
            project.ProjectPath = fullProjectPath; // 更新为完整路径
            project.InitializedAt = DateTime.UtcNow;
            project.InitError = null;

            await Repository.UpdateAsync(project);
            return true;
        }
        catch (Exception ex)
        {
            project.Status = Core.Enums.ProjectStatus.InitializeFailed;
            project.InitError = ex.Message;
            await Repository.UpdateAsync(project);
            throw;
        }
    }

    /// <summary>
    /// 启动项目
    /// </summary>
    public async Task<bool> StartAsync(long id)
    {
        var project = await Repository.GetByIdAsync(id);
        if (project == null)
            throw new Exception("Project not found");

        if (project.Status != Core.Enums.ProjectStatus.Initialized &&
            project.Status != Core.Enums.ProjectStatus.Stopped)
            throw new Exception("Project cannot be started");

        // TODO: 实现项目启动逻辑
        // 1. 启动后端服务
        // 2. 启动前端服务

        project.Status = Core.Enums.ProjectStatus.Running;
        await Repository.UpdateAsync(project);
        return true;
    }

    /// <summary>
    /// 停止项目
    /// </summary>
    public async Task<bool> StopAsync(long id)
    {
        var project = await Repository.GetByIdAsync(id);
        if (project == null)
            throw new Exception("Project not found");

        if (project.Status != Core.Enums.ProjectStatus.Running)
            throw new Exception("Project is not running");

        // TODO: 实现项目停止逻辑
        // 1. 停止前端服务
        // 2. 停止后端服务

        project.Status = Core.Enums.ProjectStatus.Stopped;
        await Repository.UpdateAsync(project);
        return true;
    }

    #region 分步初始化方法

    /// <summary>
    /// 获取项目完整路径
    /// </summary>
    private string GetFullProjectPath(Domain.Entities.CodeGen.Project project, string? targetPath)
    {
        var path = targetPath ?? project.ProjectPath;
        if (string.IsNullOrWhiteSpace(path))
            throw new Exception("Target path is required");

        // A relative path was silently resolved against the *backend process* working
        // directory, which is CodeMaster.WebApi. A project path such as "Foo.db" (the SQLite
        // file name) therefore extracted an entire generated solution into
        // CodeMaster.WebApi\Foo.db\Foo, and the host project's default globs then tried to
        // compile those files and broke the build of CodeMaster itself. Refuse anything that
        // is not absolute so the mistake surfaces immediately instead of quietly polluting
        // the repository.
        if (!Path.IsPathRooted(path))
        {
            throw new Exception(
                $"项目路径必须是绝对路径，例如 D:\\MyCodes\\{project.ProjectName}。当前值：{path}");
        }

        // 检查路径是否已经包含项目名称
        if (!path.EndsWith(project.ProjectName, StringComparison.OrdinalIgnoreCase))
        {
            path = Path.Combine(path, project.ProjectName);
        }
        return path;
    }

    /// <summary>
    /// 获取模板文件路径
    /// </summary>
    private string GetTemplateZipPath()
    {
        var configuredPath = _configuration["TemplatesPath"] ?? _configuration["CodeGen:TemplatesPath"];
        return ProjectTemplateLocator.FindLatestValidTemplateZip(configuredPath);
    }

    private string GetWritableTemplatesDirectory()
    {
        var configuredPath = _configuration["TemplatesPath"] ?? _configuration["CodeGen:TemplatesPath"];
        return ProjectTemplateLocator.ResolveWritableDirectory(configuredPath);
    }

    private static void ValidateProjectTemplateZip(string templatePath)
    {
        ProjectTemplateLocator.ValidateTemplateZip(templatePath);
    }

    public async Task<InitializeStepResultDto> Step1_ExtractTemplateAsync(InitializeStepDto input)
    {
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            var projectPath = GetFullProjectPath(project, input.TargetPath);
            var templateZipPath = GetTemplateZipPath();

            await _initializationService.Step1_ExtractTemplateAsync(
                project.ProjectName, projectPath, templateZipPath, project.Id.ToString());

            // 更新项目路径
            project.ProjectPath = projectPath;
            await Repository.UpdateAsync(project);

            return new InitializeStepResultDto
            {
                Success = true,
                Message = "模板解压完成",
                Step = "extract_template",
                Progress = 20
            };
        }
        catch (Exception ex)
        {
            return new InitializeStepResultDto
            {
                Success = false,
                Message = "模板解压失败",
                Step = "extract_template",
                Progress = 0,
                Error = ex.Message
            };
        }
    }

    public async Task<InitializeStepResultDto> Step2_GenerateSolutionAsync(InitializeStepDto input)
    {
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            var projectPath = GetFullProjectPath(project, input.TargetPath);

            await _initializationService.Step2_GenerateSolutionAsync(
                project.ProjectName, projectPath, project.Id.ToString());

            return new InitializeStepResultDto
            {
                Success = true,
                Message = "解决方案文件生成完成",
                Step = "generate_solution",
                Progress = 30
            };
        }
        catch (Exception ex)
        {
            return new InitializeStepResultDto
            {
                Success = false,
                Message = "解决方案文件生成失败",
                Step = "generate_solution",
                Progress = 20,
                Error = ex.Message
            };
        }
    }

    public async Task<InitializeStepResultDto> Step3_UpdateDatabaseConfigAsync(InitializeStepDto input)
    {
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            var projectPath = GetFullProjectPath(project, input.TargetPath);

            await _initializationService.Step3_UpdateDatabaseConfigAsync(
                project.ProjectName, projectPath, project.DatabaseType.ToString(),
                project.ConnectionString, project.Id.ToString());

            return new InitializeStepResultDto
            {
                Success = true,
                Message = "数据库配置更新完成",
                Step = "update_database_config",
                Progress = 40
            };
        }
        catch (Exception ex)
        {
            return new InitializeStepResultDto
            {
                Success = false,
                Message = "数据库配置更新失败",
                Step = "update_database_config",
                Progress = 30,
                Error = ex.Message
            };
        }
    }

    public async Task<InitializeStepResultDto> Step4_UpdatePortConfigAsync(InitializeStepDto input)
    {
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            var projectPath = GetFullProjectPath(project, input.TargetPath);

            await _initializationService.Step4_UpdatePortConfigAsync(
                project.ProjectName, projectPath, project.FrontendPort ?? 0,
                project.BackendPort ?? 0, project.Id.ToString());

            await _initializationService.WriteProjectContextAsync(
                projectPath,
                project.ProjectName,
                project.Id.ToString(),
                project.DisplayName,
                project.DatabaseType.ToString(),
                project.FrontendPort ?? 0,
                project.BackendPort ?? 0,
                _configuration["Mcp:ServerBaseUrl"]);

            return new InitializeStepResultDto
            {
                Success = true,
                Message = "端口配置更新完成",
                Step = "update_port_config",
                Progress = 50
            };
        }
        catch (Exception ex)
        {
            return new InitializeStepResultDto
            {
                Success = false,
                Message = "端口配置更新失败",
                Step = "update_port_config",
                Progress = 40,
                Error = ex.Message
            };
        }
    }

    public async Task<InitializeStepResultDto> Step5_CreateMigrationAsync(InitializeStepDto input)
    {
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            var projectPath = GetFullProjectPath(project, input.TargetPath);

            await _initializationService.Step5_CreateMigrationAsync(
                project.ProjectName, projectPath, project.Id.ToString());

            return new InitializeStepResultDto
            {
                Success = true,
                Message = "数据库迁移创建完成",
                Step = "create_migration",
                Progress = 60
            };
        }
        catch (Exception ex)
        {
            return new InitializeStepResultDto
            {
                Success = false,
                Message = "数据库迁移创建失败",
                Step = "create_migration",
                Progress = 50,
                Error = ex.Message
            };
        }
    }

    public async Task<InitializeStepResultDto> Step6_ApplyMigrationAsync(InitializeStepDto input)
    {
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            var projectPath = GetFullProjectPath(project, input.TargetPath);

            Console.WriteLine($"[Step6] Starting migration for project: {project.ProjectName}");
            Console.WriteLine($"[Step6] Project path: {projectPath}");

            await _initializationService.Step6_ApplyMigrationAsync(
                project.ProjectName, projectPath, project.Id.ToString());

            Console.WriteLine($"[Step6] Migration completed successfully");

            return new InitializeStepResultDto
            {
                Success = true,
                Message = "数据库迁移应用完成",
                Step = "apply_migration",
                Progress = 70
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Step6] Migration failed: {ex.Message}");
            Console.WriteLine($"[Step6] Stack trace: {ex.StackTrace}");

            return new InitializeStepResultDto
            {
                Success = false,
                Message = "数据库迁移应用失败",
                Step = "apply_migration",
                Progress = 60,
                Error = ex.Message
            };
        }
    }

    public async Task<InitializeStepResultDto> Step7_DotnetRestoreAsync(InitializeStepDto input)
    {
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            var projectPath = GetFullProjectPath(project, input.TargetPath);

            await _initializationService.Step7_DotnetRestoreAsync(
                project.ProjectName, projectPath, project.Id.ToString());

            return new InitializeStepResultDto
            {
                Success = true,
                Message = "后端依赖还原完成",
                Step = "dotnet_restore",
                Progress = 75
            };
        }
        catch (Exception ex)
        {
            return new InitializeStepResultDto
            {
                Success = false,
                Message = "后端依赖还原失败",
                Step = "dotnet_restore",
                Progress = 70,
                Error = ex.Message
            };
        }
    }

    public async Task<InitializeStepResultDto> Step8_WriteTranslationsAsync(InitializeStepDto input)
    {
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            var projectPath = GetFullProjectPath(project, input.TargetPath);

            await _initializationService.Step8_WriteTranslationsAsync(
                project.ProjectName, projectPath, project.DisplayName, project.Id.ToString());

            return new InitializeStepResultDto
            {
                Success = true,
                Message = "项目翻译写入完成",
                Step = "write_translations",
                Progress = 80
            };
        }
        catch (Exception ex)
        {
            return new InitializeStepResultDto
            {
                Success = false,
                Message = "项目翻译写入失败",
                Step = "write_translations",
                Progress = 75,
                Error = ex.Message
            };
        }
    }

    public async Task<InitializeStepResultDto> Step9_NpmInstallAsync(InitializeStepDto input)
    {
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            var projectPath = GetFullProjectPath(project, input.TargetPath);

            await _initializationService.Step9_NpmInstallAsync(
                project.ProjectName, projectPath, project.Id.ToString());

            return new InitializeStepResultDto
            {
                Success = true,
                Message = "前端依赖安装完成",
                Step = "npm_install",
                Progress = 90
            };
        }
        catch (Exception ex)
        {
            return new InitializeStepResultDto
            {
                Success = false,
                Message = "前端依赖安装失败",
                Step = "npm_install",
                Progress = 80,
                Error = ex.Message
            };
        }
    }

    public async Task<InitializeStepResultDto> Step10_StartBackendAsync(InitializeStepDto input)
    {
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            // Initialization proper is finished by now: the framework is extracted, the
            // solution is generated, the database and ports are configured, migrations ran,
            // packages are restored and npm install completed. Starting the two dev servers
            // is deliberately left to the user — a long-running dev server must never decide
            // whether the project counts as initialized, and auto-launching two processes
            // from a web request also made the generated files impossible to rewrite
            // (the running backend kept a handle on the Migrator output).
            await MarkInitializedAsync(project);

            return new InitializeStepResultDto
            {
                Success = true,
                Message = "项目初始化已完成。后端请手动启动：dotnet run（端口见项目配置）。",
                Step = "start_backend",
                Progress = 95
            };
        }
        catch (Exception ex)
        {
            return new InitializeStepResultDto
            {
                Success = false,
                Message = "项目收尾失败",
                Step = "start_backend",
                Progress = 90,
                Error = ex.Message
            };
        }
    }

    public async Task<InitializeStepResultDto> Step11_StartFrontendAsync(InitializeStepDto input)
    {
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            // Same reasoning as Step10: everything that defines "initialized" is already done,
            // and the frontend dev server is the user's to start (npm run dev). Keeping this
            // step means the existing 11-step UI flow still works and stays idempotent.
            await MarkInitializedAsync(project);

            return new InitializeStepResultDto
            {
                Success = true,
                Message = "项目初始化已完成。前端请手动启动：npm run dev（端口见项目配置）。",
                Step = "start_frontend",
                Progress = 100
            };
        }
        catch (Exception ex)
        {
            return new InitializeStepResultDto
            {
                Success = false,
                Message = "项目收尾失败",
                Step = "start_frontend",
                Progress = 95,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Marks the project as initialized once the preparation work is done. Idempotent, and
    /// never downgrades a project that is already running.
    /// </summary>
    private async Task MarkInitializedAsync(Domain.Entities.CodeGen.Project project)
    {
        if (project.Status is Core.Enums.ProjectStatus.Initialized or Core.Enums.ProjectStatus.Running)
            return;

        project.Status = Core.Enums.ProjectStatus.Initialized;
        project.InitializedAt = DateTime.UtcNow;
        project.InitError = null;
        await Repository.UpdateAsync(project);
    }

    public async Task<InitializationStateDto> GetInitializationStateAsync(long projectId)
    {
        var project = await Repository.GetByIdAsync(projectId);
        if (project == null)
            throw new Exception("Project not found");

        var projectPath = GetFullProjectPath(project, null);
        var state = await _initializationService.GetInitializationStateAsync(projectPath);

        return new InitializationStateDto
        {
            Extracted = state.Extracted,
            Renamed = state.Renamed,
            Replaced = state.Replaced,
            SolutionGenerated = state.SolutionGenerated,
            DatabaseConfigured = state.DatabaseConfigured,
            PortConfigured = state.PortConfigured,
            MigrationCreated = state.MigrationCreated,
            MigrationApplied = state.MigrationApplied,
            DotnetRestored = state.DotnetRestored,
            TranslationsWritten = state.TranslationsWritten,
            NpmInstalled = state.NpmInstalled,
            BackendStarted = state.BackendStarted,
            FrontendStarted = state.FrontendStarted
        };
    }

    #endregion

    #region 项目管理方法

    public async Task<ProjectActionResultDto> StartFrontendAsync(ProjectActionDto input)
    {
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            var projectPath = GetFullProjectPath(project, null);
            return await ProjectProcessLauncher.StartFrontendAsync(project.ProjectName, projectPath, project.FrontendPort);
        }
        catch (Exception ex)
        {
            return new ProjectActionResultDto
            {
                Success = false,
                Message = $"前端服务启动失败: {ex.Message}"
            };
        }
    }

    public async Task<ProjectActionResultDto> StartBackendAsync(ProjectActionDto input)
    {
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            var projectPath = GetFullProjectPath(project, null);
            return await ProjectProcessLauncher.StartBackendAsync(project.ProjectName, projectPath, project.BackendPort);
        }
        catch (Exception ex)
        {
            return new ProjectActionResultDto
            {
                Success = false,
                Message = $"后端服务启动失败: {ex.Message}"
            };
        }
    }

    public async Task<ProjectActionResultDto> StopFrontendAsync(ProjectActionDto input)
    {
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            if (!project.FrontendPort.HasValue || project.FrontendPort.Value == 0)
            {
                return new ProjectActionResultDto
                {
                    Success = false,
                    Message = "前端端口未配置"
                };
            }

            var port = project.FrontendPort.Value;
            var processIds = await GetProcessIdsByPortAsync(port);

            if (processIds.Count == 0)
            {
                return new ProjectActionResultDto
                {
                    Success = false,
                    Message = $"未找到占用端口 {port} 的进程"
                };
            }

            var killedCount = 0;
            foreach (var pid in processIds)
            {
                try
                {
                    var process = Process.GetProcessById(pid);
                    process.Kill(entireProcessTree: true);
                    killedCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to kill process {pid}: {ex.Message}");
                }
            }

            return new ProjectActionResultDto
            {
                Success = true,
                Message = $"前端服务停止成功，已终止 {killedCount} 个进程"
            };
        }
        catch (Exception ex)
        {
            return new ProjectActionResultDto
            {
                Success = false,
                Message = $"前端服务停止失败: {ex.Message}"
            };
        }
    }

    public async Task<ProjectActionResultDto> StopBackendAsync(ProjectActionDto input)
    {
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            if (!project.BackendPort.HasValue || project.BackendPort.Value == 0)
            {
                return new ProjectActionResultDto
                {
                    Success = false,
                    Message = "后端端口未配置"
                };
            }

            var port = project.BackendPort.Value;
            var processIds = await GetProcessIdsByPortAsync(port);

            if (processIds.Count == 0)
            {
                return new ProjectActionResultDto
                {
                    Success = false,
                    Message = $"未找到占用端口 {port} 的进程"
                };
            }

            var killedCount = 0;
            foreach (var pid in processIds)
            {
                try
                {
                    var process = Process.GetProcessById(pid);
                    process.Kill(entireProcessTree: true);
                    killedCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to kill process {pid}: {ex.Message}");
                }
            }

            return new ProjectActionResultDto
            {
                Success = true,
                Message = $"后端服务停止成功，已终止 {killedCount} 个进程"
            };
        }
        catch (Exception ex)
        {
            return new ProjectActionResultDto
            {
                Success = false,
                Message = $"后端服务停止失败: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// 根据端口获取进程ID列表
    /// </summary>
    private async Task<List<int>> GetProcessIdsByPortAsync(int port)
    {
        var processIds = new List<int>();

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return processIds;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            // 只匹配本机监听端口。不能仅搜索 ":端口"，否则远端连接恰好使用该端口时
            // 也可能被识别并在停止项目时误杀无关进程。
            var lines = output.Split('\n');
            foreach (var line in lines)
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4)
                    continue;

                var protocol = parts[0];
                var localEndpoint = parts[1];
                var isTcpListener = protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase) &&
                                    parts.Length >= 5 &&
                                    parts[3].Equals("LISTENING", StringComparison.OrdinalIgnoreCase);
                var isUdpEndpoint = protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase);

                if ((isTcpListener || isUdpEndpoint) && EndpointMatchesPort(localEndpoint, port))
                {
                    if (int.TryParse(parts[^1], out var pid))
                    {
                        if (!processIds.Contains(pid))
                        {
                            processIds.Add(pid);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get process IDs by port: {ex.Message}");
        }

        return processIds;
    }

    private static bool EndpointMatchesPort(string endpoint, int port)
    {
        return endpoint.EndsWith($":{port}", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ProjectStatusDto> GetStatusAsync(ProjectActionDto input)
    {
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            var frontendProcessIds = project.FrontendPort is > 0
                ? await GetProcessIdsByPortAsync(project.FrontendPort.Value)
                : [];
            var backendProcessIds = project.BackendPort is > 0
                ? await GetProcessIdsByPortAsync(project.BackendPort.Value)
                : [];

            return new ProjectStatusDto
            {
                FrontendRunning = frontendProcessIds.Count > 0,
                BackendRunning = backendProcessIds.Count > 0,
                FrontendPid = frontendProcessIds.FirstOrDefault() is var frontendPid && frontendPid > 0
                    ? frontendPid
                    : null,
                BackendPid = backendProcessIds.FirstOrDefault() is var backendPid && backendPid > 0
                    ? backendPid
                    : null
            };
        }
        catch (Exception)
        {
            return new ProjectStatusDto
            {
                FrontendRunning = false,
                BackendRunning = false
            };
        }
    }

    public async Task<ProjectActionResultDto> MigrateDatabaseAsync(ProjectActionDto input)
    {
        var outputBuilder = new global::System.Text.StringBuilder();
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            var projectPath = GetFullProjectPath(project, null);
            var migratorPath = Path.Combine(projectPath, $"{project.ProjectName}.Migrator");

            if (!Directory.Exists(migratorPath))
                throw new Exception($"Migrator 项目不存在: {migratorPath}");

            var migrationName = $"Auto{DateTime.UtcNow:yyyyMMddHHmmss}";
            var migrationCreated = false;

            // 1. 构建 Migrator 项目
            outputBuilder.AppendLine($"[1/4] 构建 {project.ProjectName}.Migrator...");
            await RunCommandAsync("dotnet", "build", migratorPath, outputBuilder, TimeSpan.FromMinutes(3));

            // 2. 先检查模型是否有变化，避免每次点击都生成空迁移。
            outputBuilder.AppendLine("[2/4] 检查模型变化...");
            var pendingChangesExitCode = await RunCommandAsync(
                "dotnet",
                "ef migrations has-pending-model-changes --no-build",
                migratorPath,
                outputBuilder,
                TimeSpan.FromMinutes(2),
                throwOnNonZero: false);

            if (pendingChangesExitCode == 1)
            {
                outputBuilder.AppendLine($"[3/4] 创建迁移 {migrationName}...");
                await RunCommandAsync("dotnet", $"ef migrations add {migrationName} --no-build", migratorPath, outputBuilder, TimeSpan.FromMinutes(2));
                migrationCreated = true;
            }
            else if (pendingChangesExitCode == 0)
            {
                outputBuilder.AppendLine("[3/4] 模型没有变化，跳过创建迁移。");
            }
            else
            {
                throw new Exception($"检查模型变化失败，退出码: {pendingChangesExitCode}");
            }

            // 3. 运行 Migrator 应用迁移
            outputBuilder.AppendLine("[4/4] 应用数据库迁移并同步种子数据...");
            await RunCommandAsync("dotnet", "run", migratorPath, outputBuilder, TimeSpan.FromMinutes(5));

            return new ProjectActionResultDto
            {
                Success = true,
                Message = migrationCreated
                    ? $"数据库迁移成功（{migrationName}）"
                    : "数据库已是最新，种子数据同步成功",
                Output = outputBuilder.ToString()
            };
        }
        catch (Exception ex)
        {
            return new ProjectActionResultDto
            {
                Success = false,
                Message = $"数据库迁移失败: {ex.Message}",
                Output = outputBuilder.ToString()
            };
        }
    }

    public async Task<List<DictTypeOptionDto>> GetDictTypesAsync(long projectId)
    {
        var dictionaries = await GetProjectDictionariesAsync(projectId, new ProjectDictionaryQueryDto
        {
            IncludeItems = false,
            IncludeDisabled = false
        });

        return dictionaries.Select(t => new DictTypeOptionDto
        {
            DictType = t.DictType,
            DictName = t.DictName
        }).ToList();
    }

    public async Task<List<ProjectDictionaryDto>> GetProjectDictionariesAsync(
        long projectId,
        ProjectDictionaryQueryDto query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.TenantId < 0)
            throw new ArgumentException("TenantId cannot be negative.", nameof(query));

        var project = await GetRequiredProjectAsync(projectId);
        using var targetDb = CreateTargetDb(project);

        var dictTypes = await targetDb.Queryable<SysDictType>()
            .Where(t => t.TenantId == query.TenantId && !t.IsDeleted)
            .WhereIF(!query.IncludeDisabled, t => t.Status == 0)
            .OrderBy(t => t.Sort)
            .OrderBy(t => t.DictType)
            .ToListAsync();

        var dictData = await targetDb.Queryable<SysDictData>()
            .Where(d => d.TenantId == query.TenantId && !d.IsDeleted)
            .WhereIF(!query.IncludeDisabled, d => d.Status == 0)
            .OrderBy(d => d.Sort)
            .OrderBy(d => d.Value)
            .ToListAsync();

        var exactType = NormalizeOptional(query.DictType);
        var keyword = NormalizeOptional(query.Keyword);
        if (exactType != null)
        {
            dictTypes = dictTypes
                .Where(item => string.Equals(item.DictType, exactType, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (keyword != null)
        {
            var matchingTypes = dictData
                .Where(item => Contains(item.Label, keyword) || Contains(item.Value, keyword))
                .Select(item => item.DictType)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            dictTypes = dictTypes
                .Where(item => Contains(item.DictType, keyword) ||
                               Contains(item.DictName, keyword) ||
                               matchingTypes.Contains(item.DictType))
                .ToList();
        }

        return dictTypes.Select(type =>
        {
            var items = dictData
                .Where(item => string.Equals(item.DictType, type.DictType, StringComparison.OrdinalIgnoreCase))
                .Select(MapDictionaryItem)
                .ToList();
            return new ProjectDictionaryDto
            {
                Id = type.Id,
                TenantId = type.TenantId,
                DictType = type.DictType,
                DictName = type.DictName,
                LangKey = type.LangKey,
                Status = type.Status,
                Sort = type.Sort,
                Remark = type.Remark,
                ItemCount = items.Count,
                Items = query.IncludeItems ? items : null
            };
        }).ToList();
    }

    public async Task<SaveProjectDictionaryResultDto> SaveProjectDictionaryAsync(
        long projectId,
        SaveProjectDictionaryDto input)
    {
        ValidateDictionaryInput(input);
        var project = await GetRequiredProjectAsync(projectId);
        using var targetDb = CreateTargetDb(project);
        var now = DateTime.UtcNow;
        var dictType = input.DictType.Trim();
        var result = new SaveProjectDictionaryResultDto
        {
            TenantId = input.TenantId,
            DictType = dictType,
            DictName = input.DictName.Trim()
        };

        targetDb.Ado.BeginTran();
        try
        {
            var typeMatches = (await targetDb.Queryable<SysDictType>()
                    .Where(item => item.TenantId == input.TenantId)
                    .ToListAsync())
                .Where(item => string.Equals(item.DictType, dictType, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var dict = typeMatches.FirstOrDefault(item => !item.IsDeleted) ?? typeMatches.FirstOrDefault();
            if (dict == null)
            {
                dict = new SysDictType
                {
                    Id = YitIdHelper.NextId(),
                    TenantId = input.TenantId,
                    DictType = dictType,
                    CreateBy = "CodeMaster",
                    CreateUserId = 0,
                    UpdateUserId = 0,
                    CreateTime = now
                };
                result.Created = true;
            }
            else
            {
                result.Restored = dict.IsDeleted;
                dict.UpdateBy = "CodeMaster";
                dict.UpdateUserId = 0;
                dict.UpdateTime = now;
            }

            dict.DictName = input.DictName.Trim();
            dict.DictType = dictType;
            dict.LangKey = input.LangKey == null ? dict.LangKey : NormalizeOptional(input.LangKey);
            dict.Status = input.Status ?? dict.Status;
            dict.Sort = input.Sort ?? dict.Sort;
            dict.Remark = input.Remark == null ? dict.Remark : NormalizeOptional(input.Remark);
            Restore(dict);

            if (result.Created)
                await targetDb.Insertable(dict).ExecuteCommandAsync();
            else
                await targetDb.Updateable(dict).ExecuteCommandAsync();

            result.Id = dict.Id;
            result.DictName = dict.DictName;

            var existingItems = (await targetDb.Queryable<SysDictData>()
                    .Where(item => item.TenantId == input.TenantId)
                    .ToListAsync())
                .Where(item => string.Equals(item.DictType, dictType, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var itemInput in input.Items)
            {
                var value = itemInput.Value.Trim();
                var matches = existingItems
                    .Where(item => string.Equals(item.Value, value, StringComparison.Ordinal))
                    .ToList();
                var item = matches.FirstOrDefault(candidate => !candidate.IsDeleted) ?? matches.FirstOrDefault();
                var itemResult = new SaveProjectDictionaryItemResultDto { Value = value };
                if (item == null)
                {
                    item = new SysDictData
                    {
                        Id = YitIdHelper.NextId(),
                        TenantId = input.TenantId,
                        DictType = dictType,
                        Value = value,
                        CreateBy = "CodeMaster",
                        CreateUserId = 0,
                        UpdateUserId = 0,
                        CreateTime = now
                    };
                    itemResult.Created = true;
                }
                else
                {
                    itemResult.Restored = item.IsDeleted;
                    item.UpdateBy = "CodeMaster";
                    item.UpdateUserId = 0;
                    item.UpdateTime = now;
                }

                item.Label = itemInput.Label.Trim();
                item.DictType = dictType;
                item.LangKey = itemInput.LangKey == null ? item.LangKey : NormalizeOptional(itemInput.LangKey);
                item.IsDefault = itemInput.IsDefault ?? item.IsDefault;
                item.Status = itemInput.Status ?? item.Status;
                item.Sort = itemInput.Sort ?? item.Sort;
                item.Remark = itemInput.Remark == null ? item.Remark : NormalizeOptional(itemInput.Remark);
                Restore(item);

                if (itemResult.Created)
                {
                    await targetDb.Insertable(item).ExecuteCommandAsync();
                    existingItems.Add(item);
                    result.CreatedItemCount++;
                }
                else
                {
                    await targetDb.Updateable(item).ExecuteCommandAsync();
                    result.UpdatedItemCount++;
                }

                itemResult.Id = item.Id;
                result.Items.Add(itemResult);
            }

            targetDb.Ado.CommitTran();
            return result;
        }
        catch
        {
            targetDb.Ado.RollbackTran();
            throw;
        }
    }

    private async Task<Project> GetRequiredProjectAsync(long projectId)
    {
        var project = await Repository.GetByIdAsync(projectId);
        return project ?? throw new KeyNotFoundException("项目不存在或无权访问");
    }

    private SqlSugarClient CreateTargetDb(Project project)
    {
        var dbType = GetTargetDbType(project.DatabaseType);
        return new SqlSugarClient(new ConnectionConfig
        {
            ConnectionString = NormalizeConnectionString(project),
            DbType = dbType,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = SqlSugarSetup.GetConfigureExternalServices(dbType)
        });
    }

    private static ProjectDictionaryItemDto MapDictionaryItem(SysDictData item)
    {
        return new ProjectDictionaryItemDto
        {
            Id = item.Id,
            TenantId = item.TenantId,
            DictType = item.DictType,
            Label = item.Label,
            Value = item.Value,
            LangKey = item.LangKey,
            IsDefault = item.IsDefault,
            Status = item.Status,
            Sort = item.Sort,
            Remark = item.Remark
        };
    }

    private static void ValidateDictionaryInput(SaveProjectDictionaryDto input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.TenantId < 0)
            throw new ArgumentException("TenantId cannot be negative.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.DictType))
            throw new ArgumentException("DictType is required.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.DictName))
            throw new ArgumentException("DictName is required.", nameof(input));
        if (input.Status is not null and not (0 or 1))
            throw new ArgumentException("Dictionary status must be 0 or 1.", nameof(input));

        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in input.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Value))
                throw new ArgumentException("Every dictionary item requires a value.", nameof(input));
            if (string.IsNullOrWhiteSpace(item.Label))
                throw new ArgumentException($"Dictionary item '{item.Value}' requires a label.", nameof(input));
            if (!values.Add(item.Value.Trim()))
                throw new ArgumentException($"Dictionary item value '{item.Value.Trim()}' is duplicated.", nameof(input));
            if (item.Status is not null and not (0 or 1))
                throw new ArgumentException($"Dictionary item '{item.Value.Trim()}' status must be 0 or 1.", nameof(input));
            if (item.IsDefault is not null and not (0 or 1))
                throw new ArgumentException($"Dictionary item '{item.Value.Trim()}' IsDefault must be 0 or 1.", nameof(input));
        }
    }

    private static void Restore(CodeMaster.Core.Entities.EntityBase entity)
    {
        entity.IsDeleted = false;
        entity.DeleteTime = null;
        entity.DeleteBy = null;
        entity.DeleteUserId = null;
    }

    private static bool Contains(string? source, string value) =>
        source?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string NormalizeConnectionString(Project project)
    {
        if (project.DatabaseType != Core.Enums.DatabaseType.SQLite)
            return project.ConnectionString ?? string.Empty;

        var connectionString = project.ConnectionString ?? string.Empty;
        var configuredDataSource = ExtractSqliteDataSource(connectionString);
        if (string.IsNullOrWhiteSpace(configuredDataSource) ||
            Path.IsPathRooted(configuredDataSource) ||
            string.Equals(configuredDataSource, ":memory:", StringComparison.OrdinalIgnoreCase) ||
            configuredDataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return connectionString;

        var dataSource = Path.GetFileName(configuredDataSource);
        var projectPath = GetFullProjectPath(project, null);
        var absolutePath = EnsureSqliteDirectory(
            Path.GetFullPath(Path.Combine(projectPath, $"{project.ProjectName}.Migrator", dataSource)));
        return Regex.Replace(connectionString, @"Data Source\s*=\s*([^;]+)", "Data Source=" + absolutePath, RegexOptions.IgnoreCase);
    }

    private DbType GetTargetDbType(Core.Enums.DatabaseType databaseType)
    {
        return databaseType switch
        {
            Core.Enums.DatabaseType.MySQL => DbType.MySql,
            Core.Enums.DatabaseType.SqlServer => DbType.SqlServer,
            Core.Enums.DatabaseType.PostgreSQL => DbType.PostgreSQL,
            Core.Enums.DatabaseType.SQLite => DbType.Sqlite,
            Core.Enums.DatabaseType.Oracle => DbType.Oracle,
            _ => DbType.SqlServer
        };
    }

    private async Task<int> RunCommandAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        global::System.Text.StringBuilder outputBuilder,
        TimeSpan timeout,
        bool throwOnNonZero = true)
    {
        using var cts = new CancellationTokenSource(timeout);
        var processWorkingDirectory = NormalizeProcessWorkingDirectory(workingDirectory);

        var processInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = processWorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        DotnetProcessEnvironment.Apply(processInfo);

        Console.WriteLine($"[Migrate] {fileName} {arguments} (cwd: {processWorkingDirectory})");

        using var process = Process.Start(processInfo);
        if (process == null)
            throw new Exception($"无法启动进程: {fileName} {arguments}");

        process.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                var safeOutput = RedactSensitiveOutput(e.Data);
                outputBuilder.AppendLine(safeOutput);
                Console.WriteLine($"[Command Output] {safeOutput}");
            }
        };
        process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                var safeOutput = RedactSensitiveOutput(e.Data);
                outputBuilder.AppendLine(safeOutput);
                Console.WriteLine($"[Command Error] {safeOutput}");
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cts.Token);

        if (throwOnNonZero && process.ExitCode != 0)
        {
            throw new Exception($"{fileName} {arguments} 退出码: {process.ExitCode}");
        }

        return process.ExitCode;
    }

    internal static string NormalizeProcessWorkingDirectory(string workingDirectory)
    {
        var fullPath = Path.GetFullPath(workingDirectory);
        if (!OperatingSystem.IsWindows() || !Directory.Exists(fullPath))
            return fullPath;

        var buffer = new global::System.Text.StringBuilder(32768);
        var length = GetLongPathName(fullPath, buffer, (uint)buffer.Capacity);
        return length > 0 && length < buffer.Capacity
            ? buffer.ToString()
            : fullPath;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(
        string shortPath,
        global::System.Text.StringBuilder longPath,
        uint bufferLength);

    private static string RedactSensitiveOutput(string output)
    {
        return Regex.Replace(
            output,
            @"(?i)(password|pwd)\s*=\s*[^;\r\n]*",
            "$1=***");
    }

    public async Task<ProjectActionResultDto> BuildAsync(ProjectActionDto input)
    {
        var outputBuilder = new global::System.Text.StringBuilder();
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            var projectPath = GetFullProjectPath(project, null);
            var solutionPath = Path.Combine(projectPath, $"{project.ProjectName}.sln");
            var frontendPath = Path.Combine(projectPath, $"{project.ProjectName}.Vue");

            if (!File.Exists(solutionPath))
                throw new FileNotFoundException("解决方案文件不存在", solutionPath);

            outputBuilder.AppendLine("[1/2] 构建后端解决方案...");
            await RunCommandAsync(
                "dotnet",
                $"build \"{solutionPath}\"",
                projectPath,
                outputBuilder,
                TimeSpan.FromMinutes(5));

            if (Directory.Exists(frontendPath) && File.Exists(Path.Combine(frontendPath, "package.json")))
            {
                outputBuilder.AppendLine("[2/2] 构建前端项目...");
                // npm 必须交给 cmd.exe 解析。直接以裸名启动 npm.cmd 时，cmd 收到的 %0 没有
                // 完整路径，npm.cmd 内部的 %~dp0 会退化成当前工作目录，于是 node 去项目目录里
                // 找 node_modules/npm 并抛 Cannot find module ...npm-cli.js。
                var npmExecutable = OperatingSystem.IsWindows() ? "cmd.exe" : "npm";
                var npmArguments = OperatingSystem.IsWindows() ? "/c npm run build" : "run build";
                await RunCommandAsync(
                    npmExecutable,
                    npmArguments,
                    frontendPath,
                    outputBuilder,
                    TimeSpan.FromMinutes(5));
            }
            else
            {
                outputBuilder.AppendLine("[2/2] 未发现前端 package.json，跳过前端构建。");
            }

            return new ProjectActionResultDto
            {
                Success = true,
                Message = "项目前后端编译成功",
                Output = outputBuilder.ToString()
            };
        }
        catch (Exception ex)
        {
            return new ProjectActionResultDto
            {
                Success = false,
                Message = $"项目编译失败: {ex.Message}",
                Output = BuildDiagnosticFormatter.Summarize(outputBuilder.ToString(), ex.Message)
            };
        }
    }

    public async Task<ProjectActionResultDto> TestAsync(ProjectActionDto input)
    {
        var outputBuilder = new global::System.Text.StringBuilder();
        try
        {
            var project = await Repository.GetByIdAsync(input.ProjectId);
            if (project == null)
                throw new Exception("Project not found");

            var projectPath = GetFullProjectPath(project, null);
            var testProjectPath = FindTestProjectPath(projectPath, project.ProjectName);
            if (testProjectPath == null)
            {
                return new ProjectActionResultDto
                {
                    Success = true,
                    Message = "目标项目未包含自动测试工程，跳过 dotnet test。",
                    Output = "未发现 *.GeneratedTests/*.Tests 测试工程。"
                };
            }

            outputBuilder.AppendLine($"[1/1] 运行测试工程: {Path.GetFileName(testProjectPath)}");
            await RunCommandAsync(
                "dotnet",
                $"test \"{testProjectPath}\"",
                projectPath,
                outputBuilder,
                TimeSpan.FromMinutes(10));

            return new ProjectActionResultDto
            {
                Success = true,
                Message = "项目自动测试全部通过",
                Output = outputBuilder.ToString()
            };
        }
        catch (Exception ex)
        {
            return new ProjectActionResultDto
            {
                Success = false,
                Message = $"项目自动测试失败: {ex.Message}",
                Output = BuildDiagnosticFormatter.Summarize(outputBuilder.ToString(), ex.Message)
            };
        }
    }

    private static string? FindTestProjectPath(string projectPath, string projectName)
    {
        var candidates = new[]
        {
            Path.Combine(projectPath, $"{projectName}.GeneratedTests"),
            Path.Combine(projectPath, $"{projectName}.Tests")
        };

        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate))
                continue;

            var csproj = Directory.GetFiles(candidate, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (csproj != null)
                return csproj;
        }

        return null;
    }

    public async Task<ProjectUiEnhancementResultDto> EnhanceUiPageAsync(ProjectUiEnhancementDto input)
    {
        var project = await Repository.GetByIdAsync(input.ProjectId);
        if (project == null)
            throw new KeyNotFoundException("Project not found");

        var modules = await _db.Queryable<ProjectModule>()
            .Where(item => item.ProjectId == input.ProjectId)
            .OrderBy(item => item.OrderNum)
            .ToListAsync();
        var entities = await _db.Queryable<ModuleEntity>()
            .Where(item => item.ProjectId == input.ProjectId)
            .OrderBy(item => item.OrderNum)
            .ToListAsync();
        var projectPath = GetFullProjectPath(project, null);

        if (string.Equals(input.TargetKind, "EntityPage", StringComparison.OrdinalIgnoreCase))
        {
            if (!input.EntityId.HasValue)
                throw new ArgumentException("EntityPage design requires EntityId.");

            var entity = entities.FirstOrDefault(item => item.Id == input.EntityId.Value)
                ?? throw new KeyNotFoundException("Entity not found in project");
            var module = modules.FirstOrDefault(item => item.Id == entity.ModuleId)
                ?? throw new KeyNotFoundException("Entity module not found in project");

            return await ProjectUiDesignService.ApplyEntityPageAsync(project, module, entity, input, projectPath);
        }

        return await ProjectUiPageRenderer.ApplyAsync(project, modules, entities, input, projectPath);
    }

    #endregion
}
