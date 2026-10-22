using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;

namespace CodeMaster.Agent.Tests.Services;

/// <summary>
/// 字段类型快照的回归测试。
/// <para>
/// 这里刻意覆盖两种相反的关系形态：Owned 子表（外键在 Target 侧）和 Reference ManyToOne
/// （外键在 Source 侧）。早期实现只读 SourceField，结果父表主键被画成假外键、子表被整条漏掉，
/// 而子表恰恰是控件最容易选错的地方，所以两个方向都必须锁住。
/// </para>
/// </summary>
public sealed class AiStageFieldTypeSyncTests
{
    [Fact]
    public async Task Snapshot_includes_owned_child_table_foreign_key()
    {
        var snapshot = await BuildAsync(BuildBlueprint());

        // Owned OneToMany 的外键是 TargetField，也就是子表上的 OrderId。
        Assert.Contains("OrderId=long(select-table)->Order", snapshot, StringComparison.Ordinal);
        Assert.Contains("- OrderItem:", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_does_not_turn_parent_primary_key_into_foreign_key()
    {
        var snapshot = await BuildAsync(BuildBlueprint());

        Assert.DoesNotContain("Id=long->OrderItem", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_resolves_reference_many_to_one_on_source_side()
    {
        var snapshot = await BuildAsync(BuildBlueprint());

        // Reference ManyToOne 的外键是 SourceField，也就是 Order.CustomerId。
        Assert.Contains("CustomerId=long(select-table)->Customer", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_skips_entities_without_relations()
    {
        var snapshot = await BuildAsync(BuildBlueprint());

        Assert.DoesNotContain("Standalone", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_returns_empty_string_when_project_has_no_relations()
    {
        // 空串代表"本轮不注入"，否则每一轮都会白加一段提示词。
        var snapshot = await BuildAsync(new AiProjectBlueprintDto());

        Assert.Equal(string.Empty, snapshot);
    }

    [Fact]
    public async Task Snapshot_states_that_types_are_authoritative()
    {
        var snapshot = await BuildAsync(BuildBlueprint());

        Assert.Contains("authoritative", snapshot, StringComparison.Ordinal);
        Assert.Contains("Do not reuse a field type", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_names_the_entities_it_had_to_omit()
    {
        // 上限 12，这里给 13 个子表 → 最后一个必须被点名。
        var snapshot = await BuildAsync(BuildWideBlueprint(13));

        // 只写"前 12 个"会让模型分不清"该实体没有关系"和"该实体被截掉了"。
        Assert.Contains("not listed", snapshot, StringComparison.Ordinal);
        Assert.Contains("Child13", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Omitted_entities_are_named_but_not_rendered_as_data_rows()
    {
        var snapshot = await BuildAsync(BuildWideBlueprint(13));

        // 点名是告诉模型"它存在"，不是把它当数据列出来。
        Assert.Contains("Child13", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("- Child13:", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_has_no_omission_notice_when_everything_fits()
    {
        // 12 个实体正好等于上限，一个都不该被点名。
        var snapshot = await BuildAsync(BuildWideBlueprint(11));

        Assert.DoesNotContain("not listed", snapshot, StringComparison.Ordinal);
    }

    private static Task<string> BuildAsync(AiProjectBlueprintDto blueprint)
        => AiStageFieldTypeSync.BuildSnapshotAsync(new StubBlueprintService(blueprint), 1);

    /// <summary>
    /// 造一个"一个主表 + N 个 Owned 子表"的蓝图，用来验证实体上限的截断行为。
    /// Id 从 100 递增，主表最小，所以按 Id 升序时子表排在后面，正好落进被截断的区间。
    /// </summary>
    private static AiProjectBlueprintDto BuildWideBlueprint(int childCount)
    {
        var root = new AiEntityBlueprintDto
        {
            Id = 100,
            Name = "Root",
            Fields =
            [
                new AiFieldBlueprintDto { Name = "Id", DataType = "long", IsPrimaryKey = true },
                new AiFieldBlueprintDto { Name = "RootNo", DataType = "string", FormControlType = "input" }
            ]
        };

        var entities = new List<AiEntityBlueprintDto> { root };
        for (var index = 1; index <= childCount; index++)
        {
            var childId = 100 + index;
            root.Relations.Add(new AiRelationBlueprintDto
            {
                Id = index,
                SourceEntityId = 100,
                TargetEntityId = childId,
                TargetEntityName = $"Child{index}",
                SourceField = "Id",
                TargetField = "RootId",
                Cardinality = "OneToMany",
                Ownership = "Owned"
            });

            entities.Add(new AiEntityBlueprintDto
            {
                Id = childId,
                Name = $"Child{index}",
                IsChildTable = true,
                Fields =
                [
                    new AiFieldBlueprintDto { Name = "Id", DataType = "long", IsPrimaryKey = true },
                    new AiFieldBlueprintDto { Name = "RootId", DataType = "long", FormControlType = "select-table" },
                    new AiFieldBlueprintDto { Name = "Amount", DataType = "decimal", FormControlType = "number" }
                ]
            });
        }

        return new AiProjectBlueprintDto
        {
            Modules = [new AiModuleBlueprintDto { Id = 1, ModuleName = "Wide", Entities = entities }]
        };
    }

    private static AiProjectBlueprintDto BuildBlueprint() => new()
    {
        Modules =
        [
            new AiModuleBlueprintDto
            {
                Id = 1,
                ModuleName = "Sales",
                Entities =
                [
                    new AiEntityBlueprintDto
                    {
                        Id = 10,
                        Name = "Order",
                        Fields =
                        [
                            new AiFieldBlueprintDto { Name = "Id", DataType = "long", IsPrimaryKey = true },
                            new AiFieldBlueprintDto { Name = "OrderNo", DataType = "string", FormControlType = "input" },
                            new AiFieldBlueprintDto { Name = "CustomerId", DataType = "long", FormControlType = "select-table" }
                        ],
                        Relations =
                        [
                            new AiRelationBlueprintDto
                            {
                                Id = 1,
                                SourceEntityId = 10,
                                TargetEntityId = 20,
                                TargetEntityName = "OrderItem",
                                SourceField = "Id",
                                TargetField = "OrderId",
                                Cardinality = "OneToMany",
                                Ownership = "Owned"
                            }
                        ]
                    },
                    new AiEntityBlueprintDto
                    {
                        Id = 20,
                        Name = "OrderItem",
                        IsChildTable = true,
                        Fields =
                        [
                            new AiFieldBlueprintDto { Name = "Id", DataType = "long", IsPrimaryKey = true },
                            new AiFieldBlueprintDto { Name = "OrderId", DataType = "long", FormControlType = "select-table" },
                            new AiFieldBlueprintDto { Name = "Amount", DataType = "decimal", FormControlType = "number" }
                        ]
                    },
                    new AiEntityBlueprintDto
                    {
                        Id = 40,
                        Name = "Customer",
                        Fields =
                        [
                            new AiFieldBlueprintDto { Name = "Id", DataType = "long", IsPrimaryKey = true },
                            new AiFieldBlueprintDto { Name = "CustomerName", DataType = "string" }
                        ],
                        Relations =
                        [
                            new AiRelationBlueprintDto
                            {
                                Id = 2,
                                SourceEntityId = 10,
                                TargetEntityId = 40,
                                TargetEntityName = "Customer",
                                SourceField = "CustomerId",
                                TargetField = "Id",
                                Cardinality = "ManyToOne",
                                Ownership = "Reference"
                            }
                        ]
                    },
                    new AiEntityBlueprintDto
                    {
                        Id = 30,
                        Name = "Standalone",
                        Fields = [new AiFieldBlueprintDto { Name = "Note", DataType = "string" }]
                    }
                ]
            }
        ]
    };

    /// <summary>只实现 GetProjectAsync；其他方法一旦被调用就立刻失败，避免静默走错路径。</summary>
    private sealed class StubBlueprintService(AiProjectBlueprintDto blueprint) : ICodeMasterProjectBlueprintService
    {
        public Task<AiProjectBlueprintDto> GetProjectAsync(long projectId) => Task.FromResult(blueprint);

        public Task<AiProjectBlueprintDto> GetProjectAsync(long projectId, AiCodegenCatalogDto? catalog)
            => Task.FromResult(blueprint);

        public Task<AiProjectOverviewDto> GetProjectOverviewAsync(long projectId)
            => throw new NotSupportedException();

        public Task<AiCodegenCatalogDto> GetCatalogAsync()
            => throw new NotSupportedException();

        public Task<AiEntityBlueprintDto> GetEntityAsync(long projectId, long? entityId, string? entityName)
            => throw new NotSupportedException();

        public Task<AiUiPageBlueprintDto> GetUiPageAsync(long projectId, long? entityId, string? entityName, string pageType)
            => throw new NotSupportedException();
    }
}
