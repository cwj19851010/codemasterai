using Microsoft.EntityFrameworkCore;
using CodeMaster.Domain.Entities.Ai;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.Domain.Entities.System;

namespace CodeMaster.Migrator.Persistence.EfCore;

/// <summary>
/// EF Core DbContext（仅用于数据库迁移）
/// DbSet 和 Configuration 由 Source Generator 自动生成
/// </summary>
public partial class CodeMasterDbContext : DbContext
{
    public CodeMasterDbContext(DbContextOptions<CodeMasterDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 调用 Source Generator 生成的配置方法
        OnModelCreatingPartial(modelBuilder);

        modelBuilder.Entity<AiMessage>()
            .Property(x => x.RequestId)
            .HasMaxLength(64);
        modelBuilder.Entity<AiMessage>()
            .Property(x => x.Role)
            .HasMaxLength(20);
        modelBuilder.Entity<AiMessage>()
            .HasIndex(x => new { x.ConversationId, x.RequestId, x.Role })
            .IsUnique();
        modelBuilder.Entity<AiAgentEvent>()
            .HasIndex(x => new { x.TenantId, x.ConversationId, x.RequestId, x.Sequence })
            .IsUnique();
        modelBuilder.Entity<AiToolExecution>()
            .Property(x => x.RequestId)
            .HasMaxLength(64);
        modelBuilder.Entity<AiToolExecution>()
            .Property(x => x.InputHash)
            .HasMaxLength(64);
        modelBuilder.Entity<AiToolExecution>()
            .Property(x => x.ToolName)
            .HasMaxLength(100);
        modelBuilder.Entity<AiToolExecution>()
            .HasIndex(x => new { x.ConversationId, x.RequestId, x.ToolName, x.InputHash })
            .IsUnique();
        modelBuilder.Entity<AiToolExecution>()
            .HasIndex(x => new { x.WorkPlanId, x.WorkBatchId, x.WorkTaskId });

        modelBuilder.Entity<AiProjectWorkPlan>()
            .Property(x => x.PlanKey)
            .HasMaxLength(64);
        modelBuilder.Entity<AiProjectWorkPlan>()
            .Property(x => x.Title)
            .HasMaxLength(200);
        modelBuilder.Entity<AiProjectWorkPlan>()
            .Property(x => x.ActivitySlot)
            .HasMaxLength(100);
        modelBuilder.Entity<AiProjectWorkPlan>()
            .HasIndex(x => new { x.ProjectId, x.PlanKey })
            .IsUnique();
        modelBuilder.Entity<AiProjectWorkPlan>()
            .HasIndex(x => new { x.TenantId, x.ProjectId, x.ActivitySlot })
            .IsUnique();
        modelBuilder.Entity<AiProjectWorkPlan>()
            .HasIndex(x => new { x.ProjectId, x.Status });
        modelBuilder.Entity<AiProjectWorkStage>()
            .Property(x => x.StageKey)
            .HasMaxLength(64);
        modelBuilder.Entity<AiProjectWorkStage>()
            .Property(x => x.Title)
            .HasMaxLength(200);
        modelBuilder.Entity<AiProjectWorkStage>()
            .Property(x => x.GenerationMode)
            .HasMaxLength(30);
        modelBuilder.Entity<AiProjectWorkStage>()
            .HasIndex(x => new { x.PlanId, x.StageKey })
            .IsUnique();
        modelBuilder.Entity<AiProjectWorkStage>()
            .HasIndex(x => new { x.PlanId, x.SortOrder });
        modelBuilder.Entity<AiProjectWorkTask>()
            .Property(x => x.TaskKey)
            .HasMaxLength(100);
        modelBuilder.Entity<AiProjectWorkTask>()
            .Property(x => x.Title)
            .HasMaxLength(200);
        modelBuilder.Entity<AiProjectWorkTask>()
            .Property(x => x.OperationKind)
            .HasMaxLength(100);
        modelBuilder.Entity<AiProjectWorkTask>()
            .Property(x => x.PayloadHash)
            .HasMaxLength(64);
        modelBuilder.Entity<AiProjectWorkTask>()
            .Property(x => x.LastFailureSignature)
            .HasMaxLength(64);
        modelBuilder.Entity<AiProjectWorkTask>()
            .HasIndex(x => new { x.PlanId, x.TaskKey })
            .IsUnique();
        modelBuilder.Entity<AiProjectWorkTask>()
            .HasIndex(x => new { x.StageId, x.SortOrder });
        modelBuilder.Entity<AiProjectWorkTask>()
            .HasIndex(x => new { x.PlanId, x.Status });
        modelBuilder.Entity<AiProjectWorkTaskDependency>()
            .HasIndex(x => new { x.PlanId, x.TaskId, x.DependsOnTaskId })
            .IsUnique();
        modelBuilder.Entity<AiProjectWorkBatch>()
            .Property(x => x.BatchKey)
            .HasMaxLength(64);
        modelBuilder.Entity<AiProjectWorkBatch>()
            .Property(x => x.Title)
            .HasMaxLength(200);
        modelBuilder.Entity<AiProjectWorkBatch>()
            .Property(x => x.InputHash)
            .HasMaxLength(64);
        modelBuilder.Entity<AiProjectWorkBatch>()
            .HasIndex(x => new { x.PlanId, x.BatchKey })
            .IsUnique();
        modelBuilder.Entity<AiProjectWorkBatch>()
            .HasIndex(x => new { x.PlanId, x.Status });
        modelBuilder.Entity<AiProjectWorkBatchTask>()
            .HasIndex(x => new { x.BatchId, x.TaskId })
            .IsUnique();
        modelBuilder.Entity<AiProjectWorkBatchTask>()
            .HasIndex(x => new { x.PlanId, x.TaskId });
        modelBuilder.Entity<AiProjectWorkAttempt>()
            .Property(x => x.ExecutorMode)
            .HasMaxLength(30);
        modelBuilder.Entity<AiProjectWorkAttempt>()
            .Property(x => x.FailureSignature)
            .HasMaxLength(64);
        modelBuilder.Entity<AiProjectWorkAttempt>()
            .HasIndex(x => new { x.BatchId, x.TaskId, x.AttemptNumber })
            .IsUnique();
        modelBuilder.Entity<AiProjectWorkAttempt>()
            .HasIndex(x => new { x.PlanId, x.Status });
        modelBuilder.Entity<AiProjectWorkCheckpoint>()
            .Property(x => x.CheckpointKey)
            .HasMaxLength(100);
        modelBuilder.Entity<AiProjectWorkCheckpoint>()
            .Property(x => x.ActionName)
            .HasMaxLength(100);
        modelBuilder.Entity<AiProjectWorkCheckpoint>()
            .Property(x => x.InputHash)
            .HasMaxLength(64);
        modelBuilder.Entity<AiProjectWorkCheckpoint>()
            .Property(x => x.IdempotencyKey)
            .HasMaxLength(128);
        modelBuilder.Entity<AiProjectWorkCheckpoint>()
            .HasIndex(x => new { x.AttemptId, x.CheckpointKey })
            .IsUnique();
        modelBuilder.Entity<AiProjectWorkCheckpoint>()
            .HasIndex(x => x.IdempotencyKey)
            .IsUnique();

        // 同项目内模块名/实体名唯一（并发批准的兜底约束，防止重复创建）
        modelBuilder.Entity<ProjectModule>()
            .HasIndex(x => new { x.ProjectId, x.ModuleName })
            .IsUnique();
        modelBuilder.Entity<ModuleEntity>()
            .HasIndex(x => new { x.ProjectId, x.Name })
            .IsUnique();

        foreach (var contributor in MigrationModuleCatalog.CreateModelContributors())
            contributor.Configure(modelBuilder);

        // 全局配置snake_case列名（已由 Source Generator 处理，这里保留作为后备）
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            // View entities already have a ToView mapping from the source generator.
            // Rewriting their table name would make EF migrations emit a shadow table.
            if (entity.ClrType.GetInterfaces().Any(item => item.Name == "IViewEntity"))
                continue;

            // 表名转snake_case
            var tableName = entity.GetTableName();
            if (!string.IsNullOrEmpty(tableName))
            {
                entity.SetTableName(ToSnakeCase(tableName));
            }

            // 列名转snake_case
            foreach (var property in entity.GetProperties())
            {
                var columnName = property.GetColumnName();
                if (!string.IsNullOrEmpty(columnName))
                {
                    property.SetColumnName(ToSnakeCase(columnName));
                }
            }
        }
    }

    /// <summary>
    /// Source Generator 生成的部分方法
    /// </summary>
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);

    /// <summary>
    /// 转换为snake_case
    /// </summary>
    private static string ToSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        var result = new System.Text.StringBuilder();
        result.Append(char.ToLower(input[0]));

        for (int i = 1; i < input.Length; i++)
        {
            if (char.IsUpper(input[i]))
            {
                result.Append('_');
                result.Append(char.ToLower(input[i]));
            }
            else
            {
                result.Append(input[i]);
            }
        }

        return result.ToString();
    }
}
