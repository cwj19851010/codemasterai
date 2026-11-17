using System.Text.Json;
using CodeMaster.Agent.Contracts;
using CodeMaster.Agent.Serialization;
using CodeMaster.Agent.Services;

namespace CodeMaster.Agent.Tests.Services;

public sealed class ProjectChangeSetDefinitionNormalizerTests
{
    [Fact]
    public void Converts_display_field_map_into_executable_entity_field_updates()
    {
        var blueprint = CreateBlueprint();
        var json = """
        {
          "action": "updateExistingDisplayMetadata",
          "generationMode": "None",
          "entities": [
            {
              "entityId": "11",
              "description": "销售订单",
              "fields": {
                "OrderNo": "销售订单号",
                "CustomerId": "客户"
              }
            }
          ]
        }
        """;

        var result = ProjectChangeSetDefinitionNormalizer.NormalizeForTest(
            "localize-existing-metadata",
            json,
            blueprint);

        Assert.Empty(result.Entities);
        var update = Assert.Single(result.EntityUpdates);
        Assert.Equal(11, update.EntityId);
        Assert.Equal("销售订单", update.Description);
        Assert.Collection(update.UpdatedFields.OrderBy(item => item.FieldId),
            item =>
            {
                Assert.Equal(12, item.FieldId);
                Assert.Equal("销售订单号", item.Description);
            },
            item =>
            {
                Assert.Equal(13, item.FieldId);
                Assert.Equal("客户", item.Description);
            });
    }

    [Fact]
    public void Converts_control_bindings_and_dictionaries_into_change_set_updates()
    {
        var json = """
        {
          "action": "configureFieldControlsAndDictionaries",
          "generationMode": "None",
          "dictionaries": [
            {
              "dictType": "order_status",
              "dictName": "订单状态",
              "items": [{ "label": "草稿", "value": "Draft" }]
            }
          ],
          "controlBindings": [
            {
              "entity": "Order",
              "field": "CustomerId",
              "formControlType": "select",
              "selectDataSource": "api",
              "relatedEntityName": "Customer",
              "relatedEntityIdField": "Id",
              "relatedEntityDisplayFields": "[\"Name\"]"
            }
          ]
        }
        """;

        var result = ProjectChangeSetDefinitionNormalizer.NormalizeForTest(
            "configure-controls",
            json,
            CreateBlueprint());

        var dictionary = Assert.Single(result.Dictionaries);
        Assert.Equal(0, dictionary.TenantId);
        Assert.Equal("configureFieldControlsAndDictionaries", dictionary.Summary);
        var fieldUpdate = Assert.Single(Assert.Single(result.EntityUpdates).UpdatedFields);
        Assert.Equal(13, fieldUpdate.FieldId);
        Assert.Equal("select", fieldUpdate.FormControlType);
        Assert.Equal("Customer", fieldUpdate.RelatedEntityName);
    }

    /// <summary>
    /// A control the stored type cannot carry is dropped instead of written: the pair would survive
    /// the save, field-control validation would reject it, and the failed stage would skip every task
    /// queued behind it. The field keeps the control its type already implies.
    /// </summary>
    [Fact]
    public void Drops_a_control_that_contradicts_the_stored_field_type()
    {
        var json = """
        {
          "action": "configureFieldControls",
          "generationMode": "None",
          "controlBindings": [
            { "entity": "Order", "field": "OrderNo", "formControlType": "number" },
            { "entity": "Order", "field": "CustomerId", "formControlType": "number" }
          ]
        }
        """;

        var result = ProjectChangeSetDefinitionNormalizer.NormalizeForTest(
            "configure-controls",
            json,
            CreateBlueprint());

        var fields = Assert.Single(result.EntityUpdates).UpdatedFields.OrderBy(item => item.FieldId).ToList();
        // OrderNo stores text, so "number" contradicts it. The normalizer drops the *property*
        // rather than substituting a "right" control — a text column can carry input/textarea/
        // password, so there is no single implied answer. null means "no change": the applier's
        // MergeRequired(update, current) keeps whatever control the field already had.
        Assert.Equal(12, fields[0].FieldId);
        Assert.Null(fields[0].FormControlType);
        // CustomerId is an integral id, so "number" is exactly right.
        Assert.Equal(13, fields[1].FieldId);
        Assert.Equal("number", fields[1].FormControlType);
    }

    /// <summary>
    /// A binding whose field is gone must not fail the stage. The plan's control bindings are compiled
    /// from the declared field list, but a read-only view entity rebuilds its fields from the SELECT's
    /// result columns when it is created — its key column is renamed to Id, so a binding that was
    /// correct at compile time can point at a column that no longer exists. Throwing here failed the
    /// whole stage and skipped every task queued behind it, which made the run permanently stuck.
    /// </summary>
    [Fact]
    public void Drops_a_binding_whose_field_no_longer_exists()
    {
        var json = """
        {
          "action": "configureFieldControls",
          "generationMode": "None",
          "controlBindings": [
            { "entity": "Order", "field": "RemovedColumnId", "formControlType": "select-table", "relatedEntityName": "Customer" },
            { "entity": "Order", "field": "CustomerId", "formControlType": "number" }
          ]
        }
        """;

        var result = ProjectChangeSetDefinitionNormalizer.NormalizeForTest(
            "setup_field_controls",
            json,
            CreateBlueprint());

        var fieldUpdate = Assert.Single(Assert.Single(result.EntityUpdates).UpdatedFields);
        Assert.Equal(13, fieldUpdate.FieldId);
        Assert.Equal("number", fieldUpdate.FormControlType);
    }

    /// <summary>
    /// A plan's field-control stage is compiled from the declared field list, which can name a
    /// label column the target entity never got. In a real run this rejected the whole change set
    /// with "references missing field(s) Name on related entity 'MaterialReceiptItem'", failed the
    /// stage, skipped every task behind it and forced two full plan regenerations. Repair it to a
    /// real label column instead of refusing the change set.
    /// </summary>
    [Fact]
    public void Repairs_a_related_display_field_the_target_entity_does_not_have()
    {
        var blueprint = CreateBlueprint();
        var customer = blueprint.Modules[0].Entities.Single(item => item.Name == "Customer");
        customer.HasPrimaryKey = true;
        customer.Fields.Clear();
        customer.Fields.Add(new AiFieldBlueprintDto
        {
            Id = 21, Name = "Id", Description = "Id", DataType = "long",
            IsPrimaryKey = true, FormControlType = "number"
        });
        customer.Fields.Add(new AiFieldBlueprintDto
        {
            Id = 22, Name = "ReceiptNo", Description = "单号", DataType = "string",
            FormControlType = "input"
        });

        var json = """
        {
          "action": "configureFieldControls",
          "generationMode": "None",
          "controlBindings": [
            {
              "entity": "Order", "field": "CustomerId", "formControlType": "select-table",
              "relatedEntityName": "Customer", "relatedEntityIdField": "Id",
              "relatedEntityDisplayFields": ["Name"]
            }
          ]
        }
        """;

        var result = ProjectChangeSetDefinitionNormalizer.NormalizeForTest(
            "setup_engineeringmanagement_controls", json, blueprint);

        var fieldUpdate = Assert.Single(Assert.Single(result.EntityUpdates).UpdatedFields);
        // The picker survives; only the impossible label is replaced by a real text column.
        Assert.Equal("select-table", fieldUpdate.FormControlType);
        Assert.Equal("Customer", fieldUpdate.RelatedEntityName);
        Assert.Equal("Id", fieldUpdate.RelatedEntityIdField);
        Assert.Equal("[\"ReceiptNo\"]", fieldUpdate.RelatedEntityDisplayFields);

        var validation = CodeMasterProjectChangeSetRules.Validate(blueprint, result);
        Assert.DoesNotContain(validation.Errors,
            error => error.Contains("references missing field", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Falls_back_to_the_key_column_when_the_related_entity_has_no_text_column()
    {
        var blueprint = CreateBlueprint();
        var customer = blueprint.Modules[0].Entities.Single(item => item.Name == "Customer");
        customer.HasPrimaryKey = true;
        customer.Fields.Clear();
        customer.Fields.Add(new AiFieldBlueprintDto
        {
            Id = 21, Name = "Id", Description = "Id", DataType = "long",
            IsPrimaryKey = true, FormControlType = "number"
        });
        customer.Fields.Add(new AiFieldBlueprintDto
        {
            Id = 22, Name = "Amount", Description = "金额", DataType = "decimal",
            FormControlType = "number"
        });

        var json = """
        {
          "action": "configureFieldControls", "generationMode": "None",
          "controlBindings": [
            { "entity": "Order", "field": "CustomerId", "formControlType": "select-table",
              "relatedEntityName": "Customer", "relatedEntityIdField": "Id",
              "relatedEntityDisplayFields": "[\"Name\"]" }
          ]
        }
        """;

        var result = ProjectChangeSetDefinitionNormalizer.NormalizeForTest(
            "setup_engineeringmanagement_controls", json, blueprint);

        var fieldUpdate = Assert.Single(Assert.Single(result.EntityUpdates).UpdatedFields);
        Assert.Equal("select-table", fieldUpdate.FormControlType);
        Assert.Equal("[\"Id\"]", fieldUpdate.RelatedEntityDisplayFields);

        var validation = CodeMasterProjectChangeSetRules.Validate(blueprint, result);
        Assert.DoesNotContain(validation.Errors,
            error => error.Contains("references missing field", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A correct binding must be passed through byte-for-byte — the repair is only allowed to touch
    /// what is provably wrong.
    /// </summary>
    [Fact]
    public void Leaves_a_valid_related_display_field_untouched()
    {
        var json = """
        {
          "action": "configureFieldControls", "generationMode": "None",
          "controlBindings": [
            { "entity": "Order", "field": "CustomerId", "formControlType": "select-table",
              "relatedEntityName": "Customer", "relatedEntityIdField": "Id",
              "relatedEntityDisplayFields": "[\"Name\"]" }
          ]
        }
        """;

        var result = ProjectChangeSetDefinitionNormalizer.NormalizeForTest(
            "setup_field_controls", json, CreateBlueprint());

        var fieldUpdate = Assert.Single(Assert.Single(result.EntityUpdates).UpdatedFields);
        Assert.Equal("[\"Name\"]", fieldUpdate.RelatedEntityDisplayFields);
        Assert.Equal("Id", fieldUpdate.RelatedEntityIdField);
    }

    [Fact]
    public void Converts_top_level_relations_into_source_entity_updates()
    {
        var json = """
        {
          "action": "createRelations",
          "generationMode": "None",
          "relations": [
            {
              "sourceEntity": "Order",
              "relationName": "Customer",
              "sourceField": "CustomerId",
              "targetEntity": "Customer",
              "targetField": "Id",
              "cardinality": "ManyToOne",
              "ownership": "Reference",
              "deleteBehavior": "Restrict",
              "isRequired": true
            }
          ]
        }
        """;

        var result = ProjectChangeSetDefinitionNormalizer.NormalizeForTest(
            "relations",
            json,
            CreateBlueprint());

        var relation = Assert.Single(Assert.Single(result.EntityUpdates).NewRelations);
        Assert.Equal("Customer", relation.TargetEntityName);
        Assert.Equal("CustomerId", relation.SourceField);
    }

    [Fact]
    public void Reports_the_exact_path_for_an_unknown_display_field()
    {
        var json = """
        {
          "generationMode": "None",
          "entities": [{ "entityId": "11", "fields": { "Missing": "缺失字段" } }]
        }
        """;

        var error = Assert.Throws<InvalidOperationException>(() =>
            ProjectChangeSetDefinitionNormalizer.NormalizeForTest("labels", json, CreateBlueprint()));

        Assert.Contains("$.entities[0].fields", error.Message);
    }

    [Fact]
    public void Leaves_canonical_create_entity_arrays_unchanged()
    {
        var canonical = new ProjectChangeSetProposal
        {
            Summary = "create",
            GenerationMode = "None",
            Entities =
            [
                new CreateEntityProposal
                {
                    ModuleName = "Sales",
                    Name = "Invoice",
                    Description = "发票",
                    Fields =
                    [
                        new CreateEntityFieldProposal
                        {
                            Name = "InvoiceNo",
                            Description = "发票号",
                            DataType = "string"
                        }
                    ]
                }
            ]
        };

        var result = ProjectChangeSetDefinitionNormalizer.NormalizeForTest(
            "create",
            JsonSerializer.Serialize(canonical, AgentJsonSerializer.Options),
            CreateBlueprint());

        Assert.Equal("InvoiceNo", Assert.Single(Assert.Single(result.Entities).Fields).Name);
    }

    [Fact]
    public void Accepts_pascal_case_tool_payloads_from_models()
    {
        const string json = """
        {
          "Summary": "创建工程项目",
          "GenerationMode": "None",
          "Entities": [
            {
              "ModuleName": "ProjectManagement",
              "Name": "Project",
              "Description": "工程项目",
              "HasPrimaryKey": true,
              "HasAudit": true,
              "HasSoftDelete": true,
              "HasTenant": true,
              "Fields": [
                { "Name": "ProjectName", "Description": "工程名称", "DataType": "string" }
              ]
            }
          ]
        }
        """;

        var result = ProjectChangeSetDefinitionNormalizer.NormalizeForTest(
            "create-project",
            json,
            CreateBlueprint());

        var entity = Assert.Single(result.Entities);
        Assert.Equal("ProjectManagement", entity.ModuleName);
        Assert.Equal("Project", entity.Name);
        Assert.Equal("ProjectName", Assert.Single(entity.Fields).Name);
    }

    [Fact]
    public void Accepts_entity_name_and_field_name_aliases_from_typed_models()
    {
        const string json = """
        {
          "ProjectId": "1",
          "Entities": [
            {
              "EntityName": "Project",
              "ModuleName": "ProjectManagement",
              "Description": "工程项目",
              "Fields": [
                { "FieldName": "ProjectName", "DataType": "string", "Description": "工程名称" }
              ]
            }
          ]
        }
        """;

        var result = ProjectChangeSetDefinitionNormalizer.NormalizeForTest(
            "create-project-aliases",
            json,
            CreateBlueprint());

        var entity = Assert.Single(result.Entities);
        Assert.Equal("Project", entity.Name);
        Assert.Equal("ProjectName", Assert.Single(entity.Fields).Name);
    }

    [Fact]
    public void Accepts_short_module_name_and_description_aliases()
    {
        const string json = """
        {
          "modules": [{ "name": "EngineeringManagement", "description": "工程管理平台核心模块", "displayName": "工程管理" }],
          "entities": [{ "name": "Project", "moduleName": "EngineeringManagement", "description": "工程项目", "fields": [] }]
        }
        """;

        var result = ProjectChangeSetDefinitionNormalizer.NormalizeForTest(
            "module-aliases",
            json,
            CreateBlueprint());

        Assert.Equal("EngineeringManagement", Assert.Single(result.Modules).ModuleName);
        Assert.Equal("工程管理平台核心模块", Assert.Single(result.Modules).ModuleDescription);
    }

    [Fact]
    public void Removes_legacy_relation_ids_and_duplicate_owned_relations_from_relation_plan()
    {
        var (blueprint, order, contact) = CreateLegacyAggregateBlueprint();

        var json = $$"""
        {
          "summary": "normalize owned aggregate relations",
          "generationMode": "None",
          "entityUpdates": [
            {
              "entityId": "{{order.Id}}",
              "deletedRelationIds": ["2084350070471397376"]
            }
          ],
          "relations": [
            {
              "sourceEntity": "Order",
              "targetEntityId": "{{contact.Id}}",
              "relationName": "{{contact.Name}}",
              "sourceField": "Id",
              "targetField": "OrderId",
              "cardinality": "OneToMany",
              "ownership": "Owned",
              "deleteBehavior": "Delete"
            }
          ]
        }
        """;

        var result = ProjectChangeSetDefinitionNormalizer.NormalizeForTest(
            "relations-owned-aggregates",
            json,
            blueprint);

        var update = Assert.Single(result.EntityUpdates);
        Assert.Empty(update.DeletedRelationIds);
        Assert.Empty(update.NewRelations);
        var validation = CodeMasterProjectChangeSetRules.Validate(blueprint, result);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
    }

    [Fact]
    public void Keeps_unknown_relation_deletions_as_validation_errors()
    {
        var (blueprint, order, contact) = CreateLegacyAggregateBlueprint();
        var json = $$"""
        {
          "summary": "do not discard unknown relation ids",
          "generationMode": "None",
          "entityUpdates": [
            {
              "entityId": "{{order.Id}}",
              "deletedRelationIds": ["2084350070471397376", "999"]
            }
          ],
          "relations": [
            {
              "sourceEntity": "Order",
              "targetEntityId": "{{contact.Id}}",
              "relationName": "{{contact.Name}}",
              "sourceField": "Id",
              "targetField": "OrderId",
              "cardinality": "OneToMany",
              "ownership": "Owned",
              "deleteBehavior": "Delete"
            }
          ]
        }
        """;

        var result = ProjectChangeSetDefinitionNormalizer.NormalizeForTest("relations", json, blueprint);

        var update = Assert.Single(result.EntityUpdates);
        Assert.Equal(999, Assert.Single(update.DeletedRelationIds));
        Assert.Empty(update.NewRelations);
        var validation = CodeMasterProjectChangeSetRules.Validate(blueprint, result);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("Deleted relation '999'", StringComparison.Ordinal));
    }

    private static (AiProjectBlueprintDto Blueprint, AiEntityBlueprintDto Order, AiEntityBlueprintDto Contact)
        CreateLegacyAggregateBlueprint()
    {
        var blueprint = CreateBlueprint();
        var order = blueprint.Modules[0].Entities.Single(item => item.Name == "Order");
        var contact = blueprint.Modules[0].Entities.Single(item => item.Name == "Customer");
        order.HasPrimaryKey = true;
        order.Fields.Add(new AiFieldBlueprintDto
        {
            Id = 14, Name = "Id", Description = "Id", DataType = "long",
            IsPrimaryKey = true, FormControlType = "number"
        });
        contact.HasPrimaryKey = true;
        contact.Fields.Add(new AiFieldBlueprintDto
        {
            Id = 23, Name = "Id", Description = "Id", DataType = "long",
            IsPrimaryKey = true, FormControlType = "number"
        });
        contact.Fields.Add(new AiFieldBlueprintDto
        {
            Id = 22, Name = "OrderId", Description = "订单 ID", DataType = "long",
            FormControlType = "number"
        });
        order.LegacyOneToManyRelations.Add(new AiLegacyOneToManyBlueprintDto
        {
            Id = 2084350070471397376,
            MasterField = "Id",
            ChildEntityId = contact.Id,
            ChildEntityName = contact.Name,
            ChildForeignKey = "OrderId"
        });
        return (blueprint, order, contact);
    }

    private static AiProjectBlueprintDto CreateBlueprint() => new()
    {
        Project = new AiProjectSummaryDto { Id = 1, ProjectName = "Trade" },
        Modules =
        [
            new AiModuleBlueprintDto
            {
                Id = 10,
                ModuleName = "Sales",
                ModuleDescription = "销售",
                Entities =
                [
                    new AiEntityBlueprintDto
                    {
                        Id = 11,
                        ModuleId = 10,
                        Name = "Order",
                        Description = "订单",
                        Fields =
                        [
                            new AiFieldBlueprintDto { Id = 12, Name = "OrderNo", Description = "订单号", DataType = "string", FormControlType = "input" },
                            new AiFieldBlueprintDto { Id = 13, Name = "CustomerId", Description = "客户ID", DataType = "long", FormControlType = "number" }
                        ]
                    },
                    new AiEntityBlueprintDto
                    {
                        Id = 20,
                        ModuleId = 10,
                        Name = "Customer",
                        Description = "客户",
                        Fields = [new AiFieldBlueprintDto { Id = 21, Name = "Name", Description = "名称", DataType = "string", FormControlType = "input" }]
                    }
                ]
            }
        ]
    };
}
