using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.UiComposition.Entities;
using CodeMaster.UiComposition.Services.OpenApi;

namespace CodeMaster.UiComposition.Tests;

public sealed class OpenApiContractParserTests
{
    [Fact]
    public void Parse_ComplexContractBuildsNestedDtoGraphAndPagedOperation()
    {
        var document = new OpenApiContractParser().Parse(ComplexContract);

        Assert.Equal("Cargo API", document.Title);
        Assert.Equal(2, document.Operations.Count);
        Assert.Contains(document.Schemas, item => item.DtoName == "OrderDto");
        Assert.Contains(document.Schemas, item => item.DtoName == "OrderItemDto");
        Assert.Contains(document.Schemas, item => item.DtoName == "AddressDto");
        Assert.Contains(document.Schemas, item => item.DtoName == "CreateOrderRequestDto");

        var list = Assert.Single(document.Operations, item => item.OperationId == "getOrders");
        Assert.Equal(PageDataShape.Paged, list.DataShape);
        Assert.Equal("#/components/schemas/Order", list.ResponseSchemaKey);
        Assert.Equal("#/components/schemas/PagedOrder", list.ResponseEnvelopeSchemaKey);

        var create = Assert.Single(document.Operations, item => item.OperationId == "createOrder");
        Assert.Equal("#/components/schemas/CreateOrderRequest", create.RequestSchemaKey);
        Assert.Equal("#/components/schemas/Order", create.ResponseSchemaKey);

        var order = Assert.Single(document.Schemas, item => item.SchemaKey == "#/components/schemas/Order");
        Assert.True(order.DtoRole.HasFlag(OpenApiDtoRole.Response));
        var items = Assert.Single(order.Members, item => item.JsonName == "items");
        Assert.Equal(OpenApiMemberKind.Collection, items.MemberKind);
        Assert.Equal("#/components/schemas/OrderItem", items.TargetSchemaKey);
        var address = Assert.Single(order.Members, item => item.JsonName == "shippingAddress");
        Assert.Equal(OpenApiMemberKind.Object, address.MemberKind);
        Assert.Equal("#/components/schemas/Address", address.TargetSchemaKey);
    }

    [Fact]
    public void Parse_OperationWithoutOperationIdUsesStableMethodAndRouteKey()
    {
        const string content = """
        {
          "openapi": "3.0.1",
          "info": { "title": "Fallback", "version": "1" },
          "paths": {
            "/cargo/{id}/": {
              "get": {
                "responses": { "204": { "description": "No content" } }
              }
            }
          }
        }
        """;

        var operation = Assert.Single(new OpenApiContractParser().Parse(content).Operations);

        Assert.Equal("GET /cargo/{id}", operation.OperationKey);
        Assert.Equal("GET", operation.HttpMethod);
        Assert.Equal("/cargo/{id}/", operation.RoutePath);
    }

    [Fact]
    public void Parse_FieldTypeChangeChangesMemberAndSchemaHashes()
    {
        var parser = new OpenApiContractParser();
        var original = parser.Parse(SimpleContract("string"));
        var changed = parser.Parse(SimpleContract("integer"));

        var originalSchema = Assert.Single(original.Schemas);
        var changedSchema = Assert.Single(changed.Schemas);
        Assert.NotEqual(originalSchema.SchemaHash, changedSchema.SchemaHash);
        Assert.NotEqual(originalSchema.Members[0].MemberHash, changedSchema.Members[0].MemberHash);
        Assert.Equal("string", originalSchema.Members[0].DataType);
        Assert.Equal("int", changedSchema.Members[0].DataType);
    }

    [Fact]
    public void Parse_LongFallbackIdentityIsCompactedDeterministically()
    {
        var route = $"/{new string('a', 650)}";
        var content = $$"""
        {
          "openapi": "3.0.1",
          "info": { "title": "Long", "version": "1" },
          "paths": {
            "{{route}}": {
              "get": { "responses": { "204": { "description": "ok" } } }
            }
          }
        }
        """;

        var first = Assert.Single(new OpenApiContractParser().Parse(content).Operations);
        var second = Assert.Single(new OpenApiContractParser().Parse(content).Operations);

        Assert.Equal(OpenApiContractParser.MaxOperationKeyLength, first.OperationKey.Length);
        Assert.Equal(first.OperationKey, second.OperationKey);
        Assert.Contains('~', first.OperationKey);
    }

    [Fact]
    public void Parse_DuplicateOperationIdsAreRejected()
    {
        const string content = """
        {
          "openapi": "3.0.1",
          "info": { "title": "Duplicate", "version": "1" },
          "paths": {
            "/first": { "get": { "operationId": "same", "responses": { "204": { "description": "ok" } } } },
            "/second": { "post": { "operationId": "same", "responses": { "204": { "description": "ok" } } } }
          }
        }
        """;

        var error = Assert.Throws<ArgumentException>(() => new OpenApiContractParser().Parse(content));

        Assert.Contains("operation identity must be unique", error.Message);
    }

    private static string SimpleContract(string type)
        => $$"""
        {
          "openapi": "3.0.1",
          "info": { "title": "Types", "version": "1" },
          "paths": {},
          "components": {
            "schemas": {
              "Value": {
                "type": "object",
                "properties": { "value": { "type": "{{type}}" } }
              }
            }
          }
        }
        """;

    private const string ComplexContract = """
    {
      "openapi": "3.0.1",
      "info": { "title": "Cargo API", "version": "1.0" },
      "paths": {
        "/orders": {
          "get": {
            "operationId": "getOrders",
            "responses": {
              "200": {
                "description": "ok",
                "content": { "application/json": { "schema": { "$ref": "#/components/schemas/PagedOrder" } } }
              }
            }
          },
          "post": {
            "operationId": "createOrder",
            "requestBody": {
              "required": true,
              "content": { "application/json": { "schema": { "$ref": "#/components/schemas/CreateOrderRequest" } } }
            },
            "responses": {
              "200": {
                "description": "ok",
                "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Order" } } }
              }
            }
          }
        }
      },
      "components": {
        "schemas": {
          "Address": {
            "type": "object",
            "required": ["city"],
            "properties": { "city": { "type": "string", "maxLength": 100 } }
          },
          "OrderItem": {
            "type": "object",
            "properties": {
              "sku": { "type": "string" },
              "quantity": { "type": "integer", "format": "int32" }
            }
          },
          "Order": {
            "type": "object",
            "required": ["id", "items"],
            "properties": {
              "id": { "type": "integer", "format": "int64" },
              "shippingAddress": { "$ref": "#/components/schemas/Address" },
              "items": { "type": "array", "items": { "$ref": "#/components/schemas/OrderItem" } }
            }
          },
          "CreateOrderRequest": {
            "type": "object",
            "properties": {
              "shippingAddress": { "$ref": "#/components/schemas/Address" },
              "items": { "type": "array", "items": { "$ref": "#/components/schemas/OrderItem" } }
            }
          },
          "PagedOrder": {
            "type": "object",
            "properties": {
              "records": { "type": "array", "items": { "$ref": "#/components/schemas/Order" } },
              "total": { "type": "integer", "format": "int64" }
            }
          }
        }
      }
    }
    """;
}
