using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.UiComposition.Entities;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;

namespace CodeMaster.UiComposition.Services.OpenApi;

internal sealed class OpenApiContractParser
{
    internal const int MaxOperationKeyLength = 500;
    internal const int MaxSchemaKeyLength = 500;
    internal const int MaxMemberKeyLength = 700;

    private readonly Dictionary<string, NormalizedOpenApiSchema> _schemas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenApiSchema> _componentSchemas = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = new();

    public NormalizedOpenApiDocument Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            throw new ArgumentException("OpenAPI document content cannot be empty.");
        if (Encoding.UTF8.GetByteCount(content) > 10 * 1024 * 1024)
            throw new ArgumentException("OpenAPI document exceeds the 10 MB import limit.");

        var reader = new OpenApiStringReader(new OpenApiReaderSettings
        {
            ReferenceResolution = ReferenceResolutionSetting.ResolveLocalReferences
        });
        var document = reader.Read(content, out var diagnostic);
        if (diagnostic.Errors.Count > 0)
        {
            var message = string.Join(Environment.NewLine, diagnostic.Errors.Select(item => item.Message));
            throw new ArgumentException($"OpenAPI document is invalid:{Environment.NewLine}{message}");
        }

        _schemas.Clear();
        _componentSchemas.Clear();
        _warnings.Clear();
        foreach (var pair in document.Components?.Schemas ?? new Dictionary<string, OpenApiSchema>())
            _componentSchemas[pair.Key] = pair.Value;
        foreach (var pair in _componentSchemas.OrderBy(item => item.Key, StringComparer.Ordinal))
            EnsureSchema(pair.Value, ComponentKey(pair.Key), pair.Key, OpenApiDtoRole.Shared);

        var operations = new List<NormalizedOpenApiOperation>();
        foreach (var path in document.Paths.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            foreach (var operationPair in path.Value.Operations.OrderBy(item => item.Key.ToString(), StringComparer.Ordinal))
                operations.Add(NormalizeOperation(path.Key, operationPair.Key, operationPair.Value, path.Value));
        }
        var duplicateOperation = operations
            .GroupBy(item => item.OperationKey, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateOperation != null)
            throw new ArgumentException($"OpenAPI operation identity must be unique: {duplicateOperation.Key}.");

        ApplySchemaRoles(operations);
        foreach (var schema in _schemas.Values)
        {
            schema.Members = schema.Members.OrderBy(item => item.JsonName, StringComparer.Ordinal).ToList();
            schema.SchemaHash = HashJson(new
            {
                schema.SchemaKey,
                schema.SchemaName,
                schema.DtoName,
                schema.Description,
                schema.DtoRole,
                schema.Members
            });
        }

        return new NormalizedOpenApiDocument
        {
            Title = document.Info?.Title ?? "OpenAPI",
            Description = document.Info?.Description,
            OpenApiVersion = diagnostic.SpecificationVersion.ToString(),
            DocumentHash = HashText(content),
            Operations = operations.OrderBy(item => item.OperationKey, StringComparer.Ordinal).ToList(),
            Schemas = _schemas.Values.OrderBy(item => item.SchemaKey, StringComparer.Ordinal).ToList(),
            Warnings = _warnings.Distinct(StringComparer.Ordinal).ToList()
        };
    }

    private NormalizedOpenApiOperation NormalizeOperation(
        string route,
        OperationType operationType,
        OpenApiOperation operation,
        OpenApiPathItem pathItem)
    {
        var method = operationType.ToString().ToUpperInvariant();
        var fallbackKey = $"{method} {NormalizeRoute(route)}";
        var operationId = string.IsNullOrWhiteSpace(operation.OperationId)
            ? ToPascalCase($"{method} {route}")
            : operation.OperationId.Trim();
        var operationKey = CompactIdentity(string.IsNullOrWhiteSpace(operation.OperationId)
            ? fallbackKey
            : operation.OperationId.Trim(), MaxOperationKeyLength);

        var parameters = (pathItem.Parameters ?? new List<OpenApiParameter>())
            .Concat(operation.Parameters ?? new List<OpenApiParameter>())
            .GroupBy(item => $"{item.In}:{item.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .Select(item => NormalizeParameter(item, operationKey))
            .OrderBy(item => item.Location, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToList();

        var request = SelectContentSchema(operation.RequestBody?.Content);
        string? requestSchemaKey = null;
        if (request.Schema != null)
        {
            requestSchemaKey = ResolveSchemaKey(
                request.Schema,
                $"#/operations/{EscapePointer(operationKey)}/requestBody",
                $"{operationId}Request",
                OpenApiDtoRole.Request | OpenApiDtoRole.Inline);
        }

        var responseSelection = SelectSuccessResponse(operation.Responses, operationKey);
        var responseSchemaKey = default(string);
        var responseEnvelopeSchemaKey = default(string);
        var dataShape = PageDataShape.Single;
        if (responseSelection.Schema != null)
        {
            var normalizedResponse = ResolveResponseSchema(
                responseSelection.Schema,
                $"#/operations/{EscapePointer(operationKey)}/responses/{responseSelection.StatusCode}",
                $"{operationId}Response");
            responseSchemaKey = normalizedResponse.ItemSchemaKey;
            responseEnvelopeSchemaKey = normalizedResponse.EnvelopeSchemaKey;
            dataShape = normalizedResponse.DataShape;
        }

        var normalized = new NormalizedOpenApiOperation
        {
            OperationKey = operationKey,
            OperationId = operationId,
            HttpMethod = method,
            RoutePath = route,
            Summary = operation.Summary,
            Description = operation.Description,
            RequestSchemaKey = requestSchemaKey,
            ResponseSchemaKey = responseSchemaKey,
            ResponseEnvelopeSchemaKey = responseEnvelopeSchemaKey,
            DataShape = dataShape,
            Parameters = parameters,
            RequestContentTypes = request.ContentTypes,
            ResponseContentTypes = responseSelection.ContentTypes
        };
        normalized.ContractHash = HashJson(normalized);
        return normalized;
    }

    private NormalizedOpenApiParameter NormalizeParameter(OpenApiParameter parameter, string operationKey)
    {
        var schema = parameter.Schema;
        var schemaKey = default(string);
        string? dataType = null;
        string? format = null;
        if (schema != null)
        {
            if (IsObjectSchema(schema) || string.Equals(schema.Type, "array", StringComparison.OrdinalIgnoreCase))
            {
                schemaKey = ResolveSchemaKey(
                    schema,
                    $"#/operations/{EscapePointer(operationKey)}/parameters/{EscapePointer(parameter.Name)}",
                    $"{ToPascalCase(operationKey)}{ToPascalCase(parameter.Name)}Parameter",
                    OpenApiDtoRole.Parameter | OpenApiDtoRole.Inline);
            }
            else
            {
                (dataType, format) = MapScalarType(schema);
            }
        }

        return new NormalizedOpenApiParameter
        {
            Name = parameter.Name,
            Location = parameter.In?.ToString() ?? "Query",
            Description = parameter.Description,
            IsRequired = parameter.Required,
            DataType = dataType,
            Format = format,
            SchemaKey = schemaKey
        };
    }

    private ResponseSchemaResolution ResolveResponseSchema(OpenApiSchema schema, string key, string name)
    {
        if (string.Equals(schema.Type, "array", StringComparison.OrdinalIgnoreCase) && schema.Items != null)
        {
            var itemKey = ResolveSchemaKey(schema.Items, $"{key}/items", $"{name}Item", OpenApiDtoRole.Response | OpenApiDtoRole.Inline);
            return new ResponseSchemaResolution(itemKey, null, PageDataShape.Collection);
        }

        var envelopeKey = ResolveSchemaKey(schema, key, name, OpenApiDtoRole.Response | OpenApiDtoRole.Inline);
        if (_schemas.TryGetValue(envelopeKey, out var envelope))
        {
            var collection = envelope.Members.FirstOrDefault(item =>
                item.MemberKind == OpenApiMemberKind.Collection && item.TargetSchemaKey != null &&
                (item.JsonName.Equals("items", StringComparison.OrdinalIgnoreCase) ||
                 item.JsonName.Equals("records", StringComparison.OrdinalIgnoreCase) ||
                 item.JsonName.Equals("list", StringComparison.OrdinalIgnoreCase)));
            var hasTotal = envelope.Members.Any(item =>
                item.JsonName.Equals("total", StringComparison.OrdinalIgnoreCase) ||
                item.JsonName.Equals("totalCount", StringComparison.OrdinalIgnoreCase));
            if (collection?.TargetSchemaKey != null && hasTotal)
                return new ResponseSchemaResolution(collection.TargetSchemaKey, envelopeKey, PageDataShape.Paged);
        }

        return new ResponseSchemaResolution(envelopeKey, envelopeKey, PageDataShape.Single);
    }

    private string ResolveSchemaKey(
        OpenApiSchema schema,
        string inlineKey,
        string inlineName,
        OpenApiDtoRole role)
    {
        if (!string.IsNullOrWhiteSpace(schema.Reference?.Id))
        {
            var componentKey = ComponentKey(schema.Reference.Id);
            var component = _componentSchemas.TryGetValue(schema.Reference.Id, out var found) ? found : schema;
            EnsureSchema(component, componentKey, schema.Reference.Id, role);
            return componentKey;
        }

        if (string.Equals(schema.Type, "array", StringComparison.OrdinalIgnoreCase) && schema.Items != null)
            return ResolveSchemaKey(schema.Items, $"{inlineKey}/items", $"{inlineName}Item", role | OpenApiDtoRole.Inline);

        var normalizedKey = CompactIdentity(inlineKey, MaxSchemaKeyLength);
        EnsureSchema(schema, normalizedKey, inlineName, role | OpenApiDtoRole.Inline);
        return normalizedKey;
    }

    private void EnsureSchema(OpenApiSchema schema, string schemaKey, string schemaName, OpenApiDtoRole role)
    {
        if (_schemas.TryGetValue(schemaKey, out var existing))
        {
            existing.DtoRole |= role;
            return;
        }

        var normalized = new NormalizedOpenApiSchema
        {
            SchemaKey = schemaKey,
            SchemaName = schemaName,
            DtoName = EnsureDtoName(schemaName),
            Description = schema.Description,
            DtoRole = role
        };
        _schemas[schemaKey] = normalized;

        var required = new HashSet<string>(schema.Required ?? new HashSet<string>(), StringComparer.Ordinal);
        foreach (var property in EnumerateProperties(schema).OrderBy(item => item.Key, StringComparer.Ordinal))
            normalized.Members.Add(NormalizeMember(schemaKey, property.Key, property.Value, required.Contains(property.Key)));
    }

    private NormalizedOpenApiMember NormalizeMember(
        string parentSchemaKey,
        string jsonName,
        OpenApiSchema schema,
        bool isRequired)
    {
        var memberKey = CompactIdentity(
            $"{parentSchemaKey}/properties/{EscapePointer(jsonName)}",
            MaxMemberKeyLength);
        var memberName = ToPascalCase(jsonName);
        var member = new NormalizedOpenApiMember
        {
            MemberKey = memberKey,
            ExternalId = GetExtensionString(schema, "x-codemaster-id"),
            JsonName = jsonName,
            MemberName = memberName,
            Description = schema.Description,
            IsRequired = isRequired,
            IsNullable = schema.Nullable || !isRequired,
            Format = schema.Format,
            MinLength = schema.MinLength,
            MaxLength = schema.MaxLength,
            Minimum = schema.Minimum,
            Maximum = schema.Maximum,
            EnumValues = (schema.Enum ?? new List<IOpenApiAny>()).Select(FormatAny).ToList()
        };

        if (schema.OneOf.Count > 0 || schema.AnyOf.Count > 0)
        {
            member.MemberKind = OpenApiMemberKind.Union;
            member.DataType = "object";
            member.IsUnsupported = true;
            _warnings.Add($"Member {memberKey} uses oneOf/anyOf and requires review.");
        }
        else if (string.Equals(schema.Type, "array", StringComparison.OrdinalIgnoreCase))
        {
            member.IsCollection = true;
            if (schema.Items != null && (IsObjectSchema(schema.Items) || schema.Items.Reference != null))
            {
                member.MemberKind = OpenApiMemberKind.Collection;
                member.TargetSchemaKey = ResolveSchemaKey(
                    schema.Items,
                    $"{memberKey}/items",
                    $"{_schemas[parentSchemaKey].SchemaName}{memberName}Item",
                    OpenApiDtoRole.Shared | OpenApiDtoRole.Inline);
            }
            else
            {
                member.MemberKind = OpenApiMemberKind.Scalar;
                var scalar = schema.Items == null ? ("object", default(string)) : MapScalarType(schema.Items);
                member.DataType = $"List<{scalar.Item1}>";
                member.Format = scalar.Item2;
            }
        }
        else if (IsObjectSchema(schema) || schema.Reference != null)
        {
            if (schema.AdditionalPropertiesAllowed && schema.AdditionalProperties != null && schema.Properties.Count == 0)
            {
                member.MemberKind = OpenApiMemberKind.Dictionary;
                var scalar = MapScalarType(schema.AdditionalProperties);
                member.DataType = $"Dictionary<string, {scalar.Type}>";
            }
            else
            {
                member.MemberKind = OpenApiMemberKind.Object;
                member.TargetSchemaKey = ResolveSchemaKey(
                    schema,
                    memberKey,
                    $"{_schemas[parentSchemaKey].SchemaName}{memberName}",
                    OpenApiDtoRole.Shared | OpenApiDtoRole.Inline);
            }
        }
        else
        {
            member.MemberKind = OpenApiMemberKind.Scalar;
            (member.DataType, member.Format) = MapScalarType(schema);
        }

        member.MemberHash = HashJson(new
        {
            member.MemberKey,
            member.ExternalId,
            member.JsonName,
            member.MemberName,
            member.Description,
            member.MemberKind,
            member.DataType,
            member.Format,
            member.IsRequired,
            member.IsNullable,
            member.IsCollection,
            member.TargetSchemaKey,
            member.MinLength,
            member.MaxLength,
            member.Minimum,
            member.Maximum,
            member.EnumValues,
            member.IsUnsupported
        });
        return member;
    }

    private static IEnumerable<KeyValuePair<string, OpenApiSchema>> EnumerateProperties(OpenApiSchema schema)
    {
        var result = new Dictionary<string, OpenApiSchema>(StringComparer.Ordinal);
        foreach (var allOf in schema.AllOf)
        {
            foreach (var property in EnumerateProperties(allOf))
                result[property.Key] = property.Value;
        }
        foreach (var property in schema.Properties)
            result[property.Key] = property.Value;
        return result;
    }

    private void ApplySchemaRoles(IEnumerable<NormalizedOpenApiOperation> operations)
    {
        foreach (var operation in operations)
        {
            MarkRole(operation.RequestSchemaKey, OpenApiDtoRole.Request);
            MarkRole(operation.ResponseSchemaKey, OpenApiDtoRole.Response);
            MarkRole(operation.ResponseEnvelopeSchemaKey, OpenApiDtoRole.Response);
            foreach (var parameter in operation.Parameters)
                MarkRole(parameter.SchemaKey, OpenApiDtoRole.Parameter);
        }
    }

    private void MarkRole(string? schemaKey, OpenApiDtoRole role)
    {
        if (schemaKey != null && _schemas.TryGetValue(schemaKey, out var schema))
            schema.DtoRole |= role;
    }

    private static (OpenApiSchema? Schema, List<string> ContentTypes) SelectContentSchema(
        IDictionary<string, OpenApiMediaType>? content)
    {
        if (content == null || content.Count == 0) return (null, new List<string>());
        var selected = content.FirstOrDefault(item =>
            item.Key.Contains("json", StringComparison.OrdinalIgnoreCase) && item.Value.Schema != null);
        if (selected.Value?.Schema == null)
            selected = content.FirstOrDefault(item => item.Value.Schema != null);
        return (selected.Value?.Schema, content.Keys.OrderBy(item => item, StringComparer.Ordinal).ToList());
    }

    private (string StatusCode, OpenApiSchema? Schema, List<string> ContentTypes) SelectSuccessResponse(
        OpenApiResponses responses,
        string operationKey)
    {
        foreach (var response in responses
                     .Where(item => item.Key.StartsWith('2') || item.Key.Equals("default", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(item => item.Key.Equals("default", StringComparison.OrdinalIgnoreCase) ? "999" : item.Key, StringComparer.Ordinal))
        {
            var selected = SelectContentSchema(response.Value.Content);
            if (selected.Schema != null) return (response.Key, selected.Schema, selected.ContentTypes);
        }

        _warnings.Add($"Operation {operationKey} has no schema-bearing success response.");
        return ("default", null, new List<string>());
    }

    private static bool IsObjectSchema(OpenApiSchema schema)
        => string.Equals(schema.Type, "object", StringComparison.OrdinalIgnoreCase) ||
           schema.Properties.Count > 0 || schema.AllOf.Count > 0;

    private static (string Type, string? Format) MapScalarType(OpenApiSchema schema)
    {
        var type = schema.Type?.ToLowerInvariant();
        var format = schema.Format?.ToLowerInvariant();
        return (type, format) switch
        {
            ("integer", "int64") => ("long", schema.Format),
            ("integer", _) => ("int", schema.Format),
            ("number", "float") => ("float", schema.Format),
            ("number", "double") => ("double", schema.Format),
            ("number", _) => ("decimal", schema.Format),
            ("boolean", _) => ("bool", schema.Format),
            ("string", "date-time") => ("DateTime", schema.Format),
            ("string", "date") => ("DateOnly", schema.Format),
            ("string", "uuid") => ("Guid", schema.Format),
            ("string", "byte") => ("byte[]", schema.Format),
            ("string", "binary") => ("byte[]", schema.Format),
            ("string", _) => ("string", schema.Format),
            (_, _) => ("object", schema.Format)
        };
    }

    private static string? GetExtensionString(OpenApiSchema schema, string key)
    {
        if (!schema.Extensions.TryGetValue(key, out var extension)) return null;
        return extension switch
        {
            OpenApiString text => text.Value,
            OpenApiInteger number => number.Value.ToString(),
            _ => extension.ToString()
        };
    }

    private static string FormatAny(IOpenApiAny value)
        => value switch
        {
            OpenApiString text => text.Value,
            OpenApiInteger number => number.Value.ToString(),
            OpenApiLong number => number.Value.ToString(),
            OpenApiFloat number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            OpenApiDouble number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            OpenApiBoolean boolean => boolean.Value ? "true" : "false",
            _ => value.ToString() ?? string.Empty
        };

    private static string ComponentKey(string id)
        => CompactIdentity($"#/components/schemas/{EscapePointer(id)}", MaxSchemaKeyLength);

    private static string NormalizeRoute(string route)
        => Regex.Replace(route.Trim(), "/+", "/").TrimEnd('/').ToLowerInvariant();

    internal static string ToPascalCase(string value)
    {
        var parts = Regex.Split(value ?? string.Empty, "[^A-Za-z0-9]+")
            .Where(item => item.Length > 0)
            .ToList();
        var result = string.Concat(parts.Select(item => char.ToUpperInvariant(item[0]) + item[1..]));
        if (result.Length == 0) result = "OpenApiModel";
        if (char.IsDigit(result[0])) result = $"Model{result}";
        return result;
    }

    internal static string EnsureDtoName(string value)
    {
        var name = ToPascalCase(value);
        return name.EndsWith("Dto", StringComparison.OrdinalIgnoreCase) ? name : $"{name}Dto";
    }

    private static string EscapePointer(string value) => value.Replace("~", "~0").Replace("/", "~1");

    internal static string CompactIdentity(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        const int hashLength = 16;
        var suffix = $"~{HashText(value)[..hashLength]}";
        return $"{value[..(maxLength - suffix.Length)]}{suffix}";
    }

    internal static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    internal static string HashJson<T>(T value)
        => HashText(JsonSerializer.Serialize(value, JsonOptions));

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private sealed record ResponseSchemaResolution(
        string? ItemSchemaKey,
        string? EnvelopeSchemaKey,
        PageDataShape DataShape);
}
