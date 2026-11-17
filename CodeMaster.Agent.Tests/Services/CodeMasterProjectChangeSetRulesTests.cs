using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Services;

namespace CodeMaster.Agent.Tests.Services;

public class CodeMasterProjectChangeSetRulesTests
{
    [Fact]
    public void Validate_RejectsExplicitSelfRelationForTreeEntity()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Create product category tree",
            Entities =
            {
                new CreateEntityProposal
                {
                    Name = "ProductCategory",
                    Description = "Product category",
                    IsTree = true,
                    Fields = { Field("Name", "Category name") },
                    Relations =
                    {
                        new CreateEntityRelationProposal
                        {
                            TargetEntityName = "ProductCategory",
                            RelationName = "Parent",
                            SourceField = "Id",
                            TargetField = "Id",
                            Cardinality = "ManyToOne",
                            Ownership = "Reference",
                            DeleteBehavior = "Restrict"
                        }
                    }
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(CreateBlueprint(), proposal);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("IsTree=true", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AllowsDictionaryInsideChangeSet()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Create order status dictionary",
            Dictionaries =
            {
                new ProjectDictionaryChangeProposal
                {
                    Summary = "Order status",
                    DictType = "order_status",
                    DictName = "Order status",
                    Items =
                    {
                        new ProjectDictionaryItemProposal { Label = "Pending", Value = "Pending" }
                    }
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(CreateBlueprint(), proposal);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(1, result.DictionaryCount);
    }

    [Fact]
    public void Validate_AllowsNewModuleEntitiesAndRelationInOneChangeSet()
    {
        var blueprint = CreateBlueprint();
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Create order aggregate",
            Modules =
            {
                new CreateModuleProposal
                {
                    ModuleName = "OrderManagement",
                    ModuleDescription = "Order management"
                }
            },
            Entities =
            {
                new CreateEntityProposal
                {
                    ModuleName = "OrderManagement",
                    Name = "Order",
                    Description = "Order",
                    Fields =
                    {
                        Field("OrderNo", "Order number")
                    },
                    Relations =
                    {
                        new CreateEntityRelationProposal
                        {
                            TargetEntityName = "OrderItem",
                            RelationName = "Items",
                            SourceField = "Id",
                            TargetField = "OrderId",
                            Cardinality = "OneToMany",
                            Ownership = "Owned",
                            DeleteBehavior = "Delete"
                        }
                    }
                },
                new CreateEntityProposal
                {
                    ModuleName = "OrderManagement",
                    Name = "OrderItem",
                    Description = "Order item",
                    IsChildTable = true,
                    Fields =
                    {
                        new CreateEntityFieldProposal
                        {
                            Name = "OrderId",
                            Description = "Order id",
                            DataType = "long",
                            FormControlType = "input"
                        },
                        Field("ProductName", "Product name")
                    }
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(blueprint, proposal);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(1, result.ModuleCount);
        Assert.Equal(2, result.EntityCount);
        Assert.Equal(3, result.FieldCount);
        Assert.Equal(1, result.RelationCount);
    }

    [Fact]
    public void Validate_WarnsWhenAgentUsesReservedModuleMenuOrder()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Create order module",
            Modules =
            {
                new CreateModuleProposal
                {
                    ModuleName = "OrderManagement",
                    ModuleDescription = "订单管理",
                    OrderNum = 1
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(CreateBlueprint(), proposal);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("3..9998", StringComparison.Ordinal) &&
            warning.Contains("OrderManagement", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ReservesTerminalOrderForReportsModule()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Create reports module",
            Modules =
            {
                new CreateModuleProposal
                {
                    ModuleName = "Reporting",
                    ModuleDescription = "报表管理",
                    OrderNum = 6
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(CreateBlueprint(), proposal);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Contains(result.Warnings, warning => warning.Contains("9999", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsChineseTechnicalModuleName()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Create order module",
            Modules =
            {
                new CreateModuleProposal
                {
                    ModuleName = "订单管理",
                    ModuleDescription = "订单管理"
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(CreateBlueprint(), proposal);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("ASCII PascalCase", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsChineseModuleRename()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Rename module",
            ModuleUpdates =
            {
                new UpdateModuleProposal
                {
                    ModuleId = 10,
                    ModuleName = "系统管理"
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(CreateBlueprint(), proposal);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("ASCII PascalCase", StringComparison.Ordinal));
    }

    [Fact]
    public void MapFields_AddsStandardPrimaryKeyMetadata()
    {
        var fields = CodeMasterProjectChangeSetService.MapFields(new CreateEntityProposal
        {
            Name = "Order",
            Description = "Order",
            HasPrimaryKey = true,
            Fields = { Field("OrderNo", "Order number") }
        });

        var primaryKey = Assert.Single(fields, field => field.IsPrimaryKey);
        Assert.Equal("Id", primaryKey.Name);
        Assert.Equal("long", primaryKey.DataType);
        Assert.True(primaryKey.IsSystemField);
    }

    /// <summary>
    /// 模型不写 showInSearch 时，列表页会一个查询条件都没有（showInList 等四个开关默认 true，
    /// 只有 showInSearch 默认 false）。这里补一次确定性兜底。
    /// </summary>
    [Fact]
    public void MapFields_FallsBackToDefaultSearchFieldsWhenModelSpecifiesNone()
    {
        var fields = CodeMasterProjectChangeSetService.MapFields(new CreateEntityProposal
        {
            Name = "Supplier",
            Description = "Supplier",
            HasPrimaryKey = true,
            Fields =
            {
                Field("Name", "名称"),
                Field("Contact", "联系人"),
                new CreateEntityFieldProposal
                {
                    Name = "Notes",
                    Description = "备注",
                    DataType = "string",
                    FormControlType = "textarea",
                    MaxLength = 1000
                },
                new CreateEntityFieldProposal
                {
                    Name = "Amount",
                    Description = "金额",
                    DataType = "decimal",
                    FormControlType = "number"
                }
            }
        });

        // Id 是系统主键；Notes 是长文本域，两者都不该进查询条件。
        Assert.Equal(
            new[] { "Name", "Contact", "Amount" },
            fields.Where(field => field.ShowInSearch).Select(field => field.Name));
    }

    [Fact]
    public void MapFields_KeepsModelChosenSearchFieldsUntouched()
    {
        var fields = CodeMasterProjectChangeSetService.MapFields(new CreateEntityProposal
        {
            Name = "Supplier",
            Description = "Supplier",
            HasPrimaryKey = false,
            Fields =
            {
                new CreateEntityFieldProposal
                {
                    Name = "Code",
                    Description = "编码",
                    DataType = "string",
                    FormControlType = "input",
                    ShowInSearch = true
                },
                Field("Contact", "联系人")
            }
        });

        Assert.Equal(
            new[] { "Code" },
            fields.Where(field => field.ShowInSearch).Select(field => field.Name));
    }

    [Fact]
    public void MapFields_NormalizesAgentDataTypeAliases()
    {
        var fields = CodeMasterProjectChangeSetService.MapFields(new CreateEntityProposal
        {
            Name = "Order",
            Description = "Order",
            HasPrimaryKey = false,
            Fields =
            {
                new CreateEntityFieldProposal
                {
                    Name = "OrderDate",
                    Description = "Order date",
                    DataType = "datetime",
                    FormControlType = "datetime"
                }
            }
        });

        var field = Assert.Single(fields);
        Assert.Equal("DateTime", field.DataType);
        Assert.Equal("datetime", field.FormControlType);
    }

    [Fact]
    public void MapOneToManyRelation_UsesParentKeyAndChildForeignKey()
    {
        var relation = new CreateEntityRelationProposal
        {
            TargetEntityName = "OrderItem",
            RelationName = "Items",
            SourceField = "Id",
            TargetField = "OrderId",
            Cardinality = "OneToMany",
            Ownership = "Owned",
            DeleteBehavior = "Delete",
            OrderNum = 10
        };

        Assert.True(CodeMasterProjectChangeSetService.IsLegacyOneToMany(relation));
        var mapped = CodeMasterProjectChangeSetService.MapOneToManyRelation(
            relation,
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["OrderItem"] = 200 });

        Assert.Equal("Id", mapped.MasterField);
        Assert.Equal(200, mapped.ChildEntityId);
        Assert.Equal("OrderItem", mapped.ChildEntityName);
        Assert.Equal("OrderId", mapped.ChildForeignKey);
    }

    [Fact]
    public void Validate_AllowsReferenceManyToOneForeignKeyToPrimaryKey()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Create invoice customer reference",
            Entities =
            {
                new CreateEntityProposal
                {
                    ModuleId = 10,
                    Name = "Invoice",
                    Description = "Invoice",
                    Fields =
                    {
                        new CreateEntityFieldProposal
                        {
                            Name = "CustomerId",
                            Description = "Customer id",
                            DataType = "long",
                            FormControlType = "input"
                        }
                    },
                    Relations =
                    {
                        new CreateEntityRelationProposal
                        {
                            TargetEntityId = 20,
                            RelationName = "Customer",
                            SourceField = "CustomerId",
                            TargetField = "Id",
                            Cardinality = "ManyToOne",
                            Ownership = "Reference",
                            DeleteBehavior = "Restrict"
                        }
                    }
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(CreateBlueprint(), proposal);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Validate_RejectsMismatchedRelationFieldTypes()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Create invalid customer reference",
            Entities =
            {
                new CreateEntityProposal
                {
                    ModuleId = 10,
                    Name = "Invoice",
                    Description = "Invoice",
                    Fields =
                    {
                        new CreateEntityFieldProposal
                        {
                            Name = "CustomerId",
                            Description = "Customer id",
                            DataType = "string",
                            FormControlType = "input"
                        }
                    },
                    Relations =
                    {
                        new CreateEntityRelationProposal
                        {
                            TargetEntityId = 20,
                            RelationName = "Customer",
                            SourceField = "CustomerId",
                            TargetField = "Id",
                            Cardinality = "ManyToOne",
                            Ownership = "Reference",
                            DeleteBehavior = "Restrict"
                        }
                    }
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(CreateBlueprint(), proposal);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("field types do not match", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsUnknownControlAndMissingRelationField()
    {
        var blueprint = CreateBlueprint();
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Invalid design",
            Entities =
            {
                new CreateEntityProposal
                {
                    ModuleId = 10,
                    Name = "Invoice",
                    Description = "Invoice",
                    Fields =
                    {
                        new CreateEntityFieldProposal
                        {
                            Name = "InvoiceNo",
                            Description = "Invoice number",
                            DataType = "string",
                            FormControlType = "not-a-control"
                        }
                    },
                    Relations =
                    {
                        new CreateEntityRelationProposal
                        {
                            TargetEntityId = 20,
                            RelationName = "Customer",
                            SourceField = "MissingCustomerId",
                            TargetField = "MissingId"
                        }
                    }
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(blueprint, proposal);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("unknown control", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, x => x.Contains("missing source field", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, x => x.Contains("missing target field", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsCascaderWithoutRelatedEntityBeforeApproval()
    {
        var blueprint = CreateBlueprint();
        blueprint.Controls.Add(new AiControlCatalogDto { ControlType = "cascader" });
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Create all-controls demo",
            Entities =
            {
                new CreateEntityProposal
                {
                    ModuleId = 10,
                    Name = "ControlDemo",
                    Description = "Control demo",
                    HasPrimaryKey = true,
                    Fields =
                    {
                        new CreateEntityFieldProposal
                        {
                            Name = "RegionPath",
                            Description = "Region path",
                            DataType = "string",
                            FormControlType = "cascader"
                        }
                    }
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(blueprint, proposal);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Contains("ControlDemo.RegionPath", StringComparison.Ordinal) &&
            error.Contains("related entity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_AllowsCascaderUsingBuiltInTreeEntity()
    {
        var blueprint = CreateBlueprint();
        blueprint.Controls.Add(new AiControlCatalogDto { ControlType = "cascader" });
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Create valid cascader demo",
            Entities =
            {
                new CreateEntityProposal
                {
                    ModuleId = 10,
                    Name = "ControlDemo",
                    Description = "Control demo",
                    HasPrimaryKey = true,
                    Fields =
                    {
                        new CreateEntityFieldProposal
                        {
                            Name = "DeptPath",
                            Description = "Department path",
                            DataType = "long",
                            FormControlType = "cascader",
                            RelatedEntityName = "SysDept",
                            RelatedEntityIdField = "Id",
                            RelatedEntityDisplayFields = "[\"Name\"]"
                        }
                    }
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(blueprint, proposal);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Validate_RejectsCascaderUsingNonTreeEntity()
    {
        var blueprint = CreateBlueprint();
        blueprint.Controls.Add(new AiControlCatalogDto { ControlType = "cascader" });
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Create invalid cascader demo",
            Entities =
            {
                new CreateEntityProposal
                {
                    ModuleId = 10,
                    Name = "ControlDemo",
                    Description = "Control demo",
                    HasPrimaryKey = true,
                    Fields =
                    {
                        new CreateEntityFieldProposal
                        {
                            Name = "RegionPath",
                            Description = "Region path",
                            DataType = "long",
                            FormControlType = "cascader",
                            RelatedEntityName = "SysUser",
                            RelatedEntityIdField = "Id",
                            RelatedEntityDisplayFields = "[\"NickName\"]"
                        }
                    }
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(blueprint, proposal);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("tree entity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsSelectTableWithoutDisplayFields()
    {
        var blueprint = CreateBlueprint();
        blueprint.Controls.Add(new AiControlCatalogDto { ControlType = "select-table" });
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Create invalid select-table demo",
            Entities =
            {
                new CreateEntityProposal
                {
                    ModuleId = 10,
                    Name = "ControlDemo",
                    Description = "Control demo",
                    HasPrimaryKey = true,
                    Fields =
                    {
                        new CreateEntityFieldProposal
                        {
                            Name = "OwnerId",
                            Description = "Owner",
                            DataType = "long",
                            FormControlType = "select-table",
                            RelatedEntityName = "SysUser",
                            RelatedEntityIdField = "Id"
                        }
                    }
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(blueprint, proposal);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("display field", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsUpdatedCascaderWithoutRelatedEntity()
    {
        var blueprint = CreateBlueprint();
        blueprint.Controls.Add(new AiControlCatalogDto { ControlType = "cascader" });
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Change customer name to cascader",
            EntityUpdates =
            {
                new UpdateEntityProposal
                {
                    EntityId = 20,
                    UpdatedFields =
                    {
                        new UpdateEntityFieldProposal
                        {
                            FieldId = 21,
                            FormControlType = "cascader"
                        }
                    }
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(blueprint, proposal);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error =>
            error.Contains("Customer.Name", StringComparison.Ordinal) &&
            error.Contains("related entity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_TreatsExistingNamesAsAdoptionInsteadOfFailure()
    {
        // 一个批次在客户端超时重试、或与另一个批次并发时，会晚于真正的建库批次到达。
        // ExecuteAsync 对同名模块/实体的处理是"认领"（见其 adoptedId / adoptedEntityId 分支），
        // 所以校验阶段不能把它判成错误——否则整批回滚，而项目元数据其实是完整的。
        var blueprint = CreateBlueprint();
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Replayed change set",
            Modules =
            {
                new CreateModuleProposal { ModuleName = "System", ModuleDescription = "Duplicate" }
            },
            Entities =
            {
                new CreateEntityProposal
                {
                    ModuleId = 10,
                    Name = "Customer",
                    Description = "Duplicate",
                    Fields = { Field("Phone", "Phone") }
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(blueprint, proposal);

        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, x => x.Contains("Module 'System' already exists", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, x => x.Contains("Entity 'Customer' already exists", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, x => x.Contains("already exists", StringComparison.Ordinal));
        // 认领项只会有一次实体行，不能既算"已存在"又算"最终重名"。
        Assert.DoesNotContain(result.Errors, x => x.Contains("Duplicate final", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_StillRejectsDuplicateWhenExistingModuleIsRenamedAwayInSameChangeSet()
    {
        // 被本变更集改名让出的名字不算"认领"：那个名字确实要腾出来给新模块，
        // 若新模块又和别的东西撞名，必须继续报错。
        var blueprint = CreateBlueprint();
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Rename away and recreate",
            ModuleUpdates =
            {
                new UpdateModuleProposal { ModuleId = 10, ModuleName = "SystemRenamed" }
            },
            Modules =
            {
                new CreateModuleProposal { ModuleName = "System", ModuleDescription = "Recreated" },
                new CreateModuleProposal { ModuleName = "SystemRenamed", ModuleDescription = "Collides with the rename" }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(blueprint, proposal);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("Duplicate final module name", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AllowsFieldUpdateReorderInMetadataOnlyMode()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Move customer name and make it searchable",
            GenerationMode = "None",
            EntityUpdates =
            {
                new UpdateEntityProposal
                {
                    EntityId = 20,
                    UpdatedFields =
                    {
                        new UpdateEntityFieldProposal
                        {
                            FieldId = 21,
                            OrderNum = 50000,
                            ShowInSearch = true,
                            FormControlType = "input"
                        }
                    }
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(CreateBlueprint(), proposal);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(1, result.UpdatedEntityCount);
    }

    [Fact]
    public void Validate_RejectsModuleNamedLikeTheProject()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Add a module that shadows the project root namespace",
            Modules =
            {
                new CreateModuleProposal { ModuleName = "TestProject", ModuleDescription = "Same as the project" }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(CreateBlueprint(), proposal, "TestProject");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("cannot reuse the project name", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsRenamingModuleToTheProjectName()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Rename an existing module onto the project root namespace",
            ModuleUpdates =
            {
                // CreateBlueprint() 里唯一的模块 Id 是 10（20 是 Customer 实体的 Id）。
                // 写成 20 会先命中"模块不存在"，校验直接 continue，永远走不到改名判断。
                new UpdateModuleProposal { ModuleId = 10, ModuleName = "TestProject", ModuleDescription = "Renamed" }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(CreateBlueprint(), proposal, "TestProject");

        Assert.False(result.IsValid);
        // 重命名走的是 ValidateProjectRootNamespaceReuse 的"改名"分支，文案与"新建"分支不同：
        // 新建 → "cannot reuse the project name"，改名 → "cannot be renamed to ..., which reuses ..."。
        Assert.Contains(result.Errors, x => x.Contains("cannot be renamed to 'TestProject'", StringComparison.Ordinal));
        Assert.Contains(result.Errors, x => x.Contains("reuses the project name", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AllowsModuleNamedDifferentlyFromTheProject()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Add a normally named module",
            Modules =
            {
                new CreateModuleProposal { ModuleName = "Engineering", ModuleDescription = "Engineering" }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(CreateBlueprint(), proposal, "TestProject");

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    [Fact]
    public void Validate_RejectsDeletingModuleThatStillContainsEntities()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Delete system module",
            DeleteModuleIds = { 10 }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(CreateBlueprint(), proposal);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("still contains entities", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RequiresIncomingRelationToBeRemovedBeforeTargetDeletion()
    {
        var blueprint = CreateBlueprint();
        blueprint.Modules[0].Entities.Add(new AiEntityBlueprintDto
        {
            Id = 30,
            ModuleId = 10,
            Name = "Order",
            Description = "Order",
            HasPrimaryKey = true,
            Fields =
            {
                new AiFieldBlueprintDto
                {
                    Id = 31,
                    Name = "CustomerId",
                    Description = "Customer id",
                    DataType = "long",
                    FormControlType = "input"
                }
            },
            Relations =
            {
                new AiRelationBlueprintDto
                {
                    Id = 32,
                    SourceEntityId = 30,
                    TargetEntityId = 20,
                    TargetEntityName = "Customer",
                    RelationName = "Customer",
                    SourceField = "CustomerId",
                    TargetField = "Id",
                    Cardinality = "OneToOne",
                    Ownership = "Reference",
                    DeleteBehavior = "Restrict"
                }
            }
        });
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Delete customer",
            DeleteEntityIds = { 20 }
        };

        var invalid = CodeMasterProjectChangeSetRules.Validate(blueprint, proposal);
        proposal.EntityUpdates.Add(new UpdateEntityProposal
        {
            EntityId = 30,
            DeletedRelationIds = { 32 }
        });
        var valid = CodeMasterProjectChangeSetRules.Validate(blueprint, proposal);

        Assert.False(invalid.IsValid);
        Assert.Contains(invalid.Errors, x => x.Contains("still references it", StringComparison.OrdinalIgnoreCase));
        Assert.True(valid.IsValid, string.Join(Environment.NewLine, valid.Errors));
    }

    [Fact]
    public void Validate_DuplicateEntityUpdatesReturnValidationErrorInsteadOfThrowing()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Duplicate updates",
            EntityUpdates =
            {
                new UpdateEntityProposal { EntityId = 20, OrderNum = 10 },
                new UpdateEntityProposal { EntityId = 20, OrderNum = 20 }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(CreateBlueprint(), proposal);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("Duplicate entity update id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_DropsNewRelationEquivalentToLegacyOneToMany()
    {
        var blueprint = CreateBlueprint();
        var customer = blueprint.Modules[0].Entities[0];
        customer.Fields.Add(new AiFieldBlueprintDto
        {
            Id = 22,
            Name = "Id",
            Description = "Id",
            DataType = "long",
            IsPrimaryKey = true,
            FormControlType = "input"
        });
        blueprint.Modules[0].Entities.Add(new AiEntityBlueprintDto
        {
            Id = 30,
            ModuleId = 10,
            Name = "Contact",
            Description = "Contact",
            Fields =
            {
                new AiFieldBlueprintDto
                {
                    Id = 31,
                    Name = "CustomerId",
                    Description = "Customer id",
                    DataType = "long",
                    FormControlType = "input"
                }
            }
        });
        customer.LegacyOneToManyRelations.Add(new AiLegacyOneToManyBlueprintDto
        {
            Id = 40,
            MasterField = "Id",
            ChildEntityId = 30,
            ChildEntityName = "Contact",
            ChildForeignKey = "CustomerId"
        });
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Add duplicate aggregate relation",
            EntityUpdates =
            {
                new UpdateEntityProposal
                {
                    EntityId = customer.Id,
                    NewRelations =
                    {
                        new CreateEntityRelationProposal
                        {
                            TargetEntityId = 30,
                            RelationName = "Contacts",
                            SourceField = "id",
                            TargetField = "customerid",
                            Cardinality = "OneToMany",
                            Ownership = "Owned",
                            DeleteBehavior = "Delete"
                        }
                    }
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(blueprint, proposal);

        // 等价关系不再判整批无效：从提案里摘掉并给出告警。
        // 一条重复关系作废整个变更集，会让它后面排队的任务全部阻塞，代价远大于收益。
        Assert.True(result.IsValid);
        Assert.Empty(proposal.EntityUpdates[0].NewRelations);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("already exists as the legacy one-to-many relation", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ComputedFieldRequiresNumericTypeAndFormulaReference()
    {
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Create calculated invoice",
            Entities =
            {
                new CreateEntityProposal
                {
                    ModuleId = 10,
                    Name = "Invoice",
                    Description = "Invoice",
                    Fields =
                    {
                        new CreateEntityFieldProposal
                        {
                            Name = "Total",
                            Description = "Total",
                            DataType = "string",
                            FormControlType = "input",
                            FieldCategory = "Computed",
                            Formula = "100"
                        }
                    }
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(CreateBlueprint(), proposal);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("numeric", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("[FieldName]", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_RejectsDeletingFieldStillUsedByIncomingRelation()
    {
        var blueprint = CreateBlueprint();
        blueprint.Modules[0].Entities.Add(new AiEntityBlueprintDto
        {
            Id = 30,
            ModuleId = 10,
            Name = "Order",
            Description = "Order",
            Fields =
            {
                new AiFieldBlueprintDto
                {
                    Id = 31,
                    Name = "CustomerId",
                    Description = "Customer id",
                    DataType = "long",
                    FormControlType = "input"
                }
            },
            Relations =
            {
                new AiRelationBlueprintDto
                {
                    Id = 32,
                    SourceEntityId = 30,
                    TargetEntityId = 20,
                    TargetEntityName = "Customer",
                    RelationName = "CustomerName",
                    SourceField = "CustomerId",
                    TargetField = "Name",
                    Cardinality = "OneToOne",
                    Ownership = "Reference",
                    DeleteBehavior = "Restrict"
                }
            }
        });
        var proposal = new ProjectChangeSetProposal
        {
            Summary = "Remove customer name",
            EntityUpdates =
            {
                new UpdateEntityProposal
                {
                    EntityId = 20,
                    DeletedFieldIds = { 21 }
                }
            }
        };

        var result = CodeMasterProjectChangeSetRules.Validate(blueprint, proposal);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("removed or renamed target field", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ToolPayloadCodec_CompressesAndRestoresLargeChangeSetJson()
    {
        var json = "{\"entities\":[" + string.Join(',', Enumerable.Repeat("{\"name\":\"OrderItem\",\"formControlType\":\"input\",\"showInList\":true}", 400)) + "]}";

        var stored = AiToolPayloadCodec.Encode(json);
        var restored = AiToolPayloadCodec.Decode(stored);

        Assert.StartsWith("gzip:", stored, StringComparison.Ordinal);
        Assert.True(stored.Length < 7800);
        Assert.Equal(json, restored);
    }

    private static AiProjectBlueprintDto CreateBlueprint()
    {
        return new AiProjectBlueprintDto
        {
            Project = new AiProjectSummaryDto { Id = 1, ProjectName = "TestProject" },
            Controls =
            {
                new AiControlCatalogDto { ControlType = "input", PageSections = { "add", "edit", "search", "list", "detail" } }
            },
            Modules =
            {
                new AiModuleBlueprintDto
                {
                    Id = 10,
                    ModuleName = "System",
                    ModuleDescription = "System",
                    Entities =
                    {
                        new AiEntityBlueprintDto
                        {
                            Id = 20,
                            ModuleId = 10,
                            Name = "Customer",
                            Description = "Customer",
                            HasPrimaryKey = true,
                            Fields =
                            {
                                new AiFieldBlueprintDto { Id = 21, Name = "Name", Description = "Name", DataType = "string", FormControlType = "input" }
                            }
                        }
                    }
                }
            }
        };
    }

    private static CreateEntityFieldProposal Field(string name, string description)
    {
        return new CreateEntityFieldProposal
        {
            Name = name,
            Description = description,
            DataType = "string",
            FormControlType = "input"
        };
    }
}
