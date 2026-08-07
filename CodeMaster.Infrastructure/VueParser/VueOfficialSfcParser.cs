using System.Text.Json;
using System.Text.RegularExpressions;
using CodeMaster.Infrastructure.VueParser.Model;
using Jint;

namespace CodeMaster.Infrastructure.VueParser;

/// <summary>
/// Executes Vue's official browser compiler inside the managed process. The
/// compiler is loaded on demand from CodeMaster.Vue/node_modules so ordinary
/// backend startup does not require the JavaScript runtime bundle.
/// </summary>
public sealed class VueOfficialSfcParser
{
    public const string ExpectedCompilerVersion = "3.5.28";

    private static readonly Lazy<RuntimeState> Runtime = new(CreateRuntime, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public bool IsAvailable => TryResolveCompilerPath(out _);

    public VueOfficialSfcParseResult Parse(string source, string filename = "component.vue")
    {
        ArgumentNullException.ThrowIfNull(source);
        var runtime = Runtime.Value;
        string json;
        lock (runtime.SyncRoot)
        {
            runtime.Engine.SetValue("__codemaster_source", source);
            runtime.Engine.SetValue("__codemaster_filename", filename);
            json = runtime.Engine.Evaluate(
                "__codemaster_parse_sfc(__codemaster_source, __codemaster_filename)").AsString();
        }

        var payload = JsonSerializer.Deserialize<OfficialParsePayload>(json, JsonOptions)
            ?? throw new VueOfficialCompilerException("Vue compiler returned an empty parse result.");
        if (payload.Errors.Count > 0)
            throw new VueOfficialCompilerException(string.Join(Environment.NewLine, payload.Errors));
        if (!string.Equals(payload.Version, ExpectedCompilerVersion, StringComparison.Ordinal))
            throw new VueOfficialCompilerException(
                $"Vue compiler version mismatch. Expected {ExpectedCompilerVersion}, received {payload.Version}.");

        var components = payload.Template
            .Select(MapNode)
            .Where(item => item != null)
            .Cast<Component>()
            .ToList();
        FixTemplateSlots(components);
        return new VueOfficialSfcParseResult
        {
            CompilerVersion = payload.Version,
            Components = components,
            ScriptSetupContent = payload.ScriptSetupContent,
            ScriptContent = payload.ScriptContent
        };
    }

    public VueOfficialSfcParseResult ParseTemplateContent(string templateContent, string filename = "fragment.vue")
        => Parse($"<template>\n{templateContent}\n</template>", filename);

    private static Component? MapNode(OfficialNode node)
    {
        if (string.Equals(node.Kind, "text", StringComparison.Ordinal))
        {
            var content = node.Content?.Trim();
            if (string.IsNullOrWhiteSpace(content)) return null;
            return new Component
            {
                Tag = "text",
                Content = content,
                Props = new List<ComponentProp>
                {
                    new() { Key = "content", Value = content, Type = "string" }
                },
                Instructions = new List<ComponentInstruction>(),
                Events = new List<ComponentEvent>(),
                UseSlots = new List<ComponentSlot>()
            };
        }

        if (!string.Equals(node.Kind, "element", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(node.Tag))
            return null;

        var component = new Component
        {
            Tag = node.Tag,
            Props = new List<ComponentProp>(),
            Instructions = new List<ComponentInstruction>(),
            Events = new List<ComponentEvent>(),
            UseSlots = new List<ComponentSlot>(),
            Children = node.Children
                .Select(MapNode)
                .Where(item => item != null)
                .Cast<Component>()
                .ToList()
        };
        foreach (var attribute in node.Attributes)
            ApplyAttribute(component, attribute);
        return component;
    }

    private static void ApplyAttribute(Component component, OfficialAttribute attribute)
    {
        var name = attribute.Name ?? string.Empty;
        var value = attribute.Value;
        if (name == "data-gen-id")
        {
            component.GenId = attribute.HasValue ? value : null;
            return;
        }
        if (name == "data-entity-table")
        {
            component.EntityTable = attribute.HasValue ? value : null;
            return;
        }
        if (name == "data-entity-field")
        {
            component.EntityField = attribute.HasValue ? value : null;
            return;
        }
        if (component.Tag == "template" && (name.StartsWith('#') || name.StartsWith("v-slot:")))
        {
            component.UseSlots!.Add(new ComponentSlot
            {
                Name = name.StartsWith('#') ? name[1..] : name[7..],
                Parameter = attribute.HasValue ? value : null,
                Components = new List<Component>()
            });
            return;
        }
        if (name.StartsWith('@'))
        {
            component.Events!.Add(new ComponentEvent
            {
                Name = name[1..],
                Body = attribute.HasValue ? value : null,
                IsSingle = !attribute.HasValue
            });
            return;
        }
        if (name.StartsWith(':'))
        {
            component.Props!.Add(new ComponentProp
            {
                Key = name[1..],
                Value = attribute.HasValue ? value : null,
                IsBind = true,
                IsSingle = !attribute.HasValue
            });
            return;
        }
        if (name.StartsWith("v-"))
        {
            component.Instructions!.Add(new ComponentInstruction
            {
                Name = name,
                Value = attribute.HasValue ? value : null,
                IsSingle = !attribute.HasValue
            });
            return;
        }
        if (name == "ref")
        {
            component.Ref = attribute.HasValue ? value : null;
            return;
        }

        component.Props!.Add(new ComponentProp
        {
            Key = name,
            Value = attribute.HasValue ? value : null,
            IsBind = false,
            IsSingle = !attribute.HasValue
        });
    }

    private static RuntimeState CreateRuntime()
    {
        if (!TryResolveCompilerPath(out var path))
        {
            throw new VueOfficialCompilerUnavailableException(
                $"@vue/compiler-sfc@{ExpectedCompilerVersion} is unavailable. Run npm install in CodeMaster.Vue or set CODEMASTER_VUE_COMPILER_SFC to compiler-sfc.esm-browser.js.");
        }

        var source = File.ReadAllText(path);
        var versionMatch = Regex.Match(source, "const version = \\\"(?<version>[^\\\"]+)\\\";");
        if (!versionMatch.Success || versionMatch.Groups["version"].Value != ExpectedCompilerVersion)
        {
            throw new VueOfficialCompilerUnavailableException(
                $"Expected @vue/compiler-sfc@{ExpectedCompilerVersion}, but '{path}' contains version '{versionMatch.Groups["version"].Value}'.");
        }

        var exportIndex = source.LastIndexOf("\nexport {", StringComparison.Ordinal);
        if (exportIndex < 0)
            throw new VueOfficialCompilerUnavailableException("The pinned Vue compiler export contract was not found.");
        var exportContract = source[exportIndex..];
        var parseAlias = Regex.Match(exportContract, @"(?<symbol>[\w$]+)\s+as\s+parse(?:\s*,|\s*})");
        if (!parseAlias.Success)
            throw new VueOfficialCompilerUnavailableException("The pinned Vue compiler parse export was not found.");

        var executable = source[..exportIndex] + BuildBridge(parseAlias.Groups["symbol"].Value);
        var engine = new Engine(options => options
            .TimeoutInterval(TimeSpan.FromSeconds(20))
            .LimitRecursion(512)
            .MaxStatements(10_000_000));
        engine.Execute(executable);
        return new RuntimeState(engine);
    }

    private static string BuildBridge(string parseSymbol) => $$$"""

        function __codemaster_expression(node) {
          if (!node) return null;
          if (typeof node.content === 'string') return node.content;
          return node.loc && typeof node.loc.source === 'string' ? node.loc.source : null;
        }
        function __codemaster_argument(node) {
          if (!node) return null;
          const content = __codemaster_expression(node) || '';
          return node.isStatic === false ? '[' + content + ']' : content;
        }
        function __codemaster_directive_name(prop) {
          const arg = __codemaster_argument(prop.arg);
          const modifiers = (prop.modifiers || []).map(item => '.' + (__codemaster_expression(item) || item)).join('');
          if (prop.name === 'bind') return (arg ? ':' + arg : 'v-bind') + modifiers;
          if (prop.name === 'on') return (arg ? '@' + arg : 'v-on') + modifiers;
          if (prop.name === 'slot') return (arg ? '#' + arg : 'v-slot') + modifiers;
          return 'v-' + prop.name + (arg ? ':' + arg : '') + modifiers;
        }
        function __codemaster_attribute(prop) {
          if (prop.type === 6) {
            return { name: prop.name, value: prop.value ? prop.value.content : null, hasValue: !!prop.value };
          }
          if (prop.type === 7) {
            return { name: __codemaster_directive_name(prop), value: __codemaster_expression(prop.exp), hasValue: !!prop.exp };
          }
          return null;
        }
        function __codemaster_node(node) {
          if (!node) return null;
          if (node.type === 1) {
            return {
              kind: 'element',
              tag: node.tag,
              attributes: (node.props || []).map(__codemaster_attribute).filter(Boolean),
              children: (node.children || []).map(__codemaster_node).filter(Boolean)
            };
          }
          if (node.type === 2) return { kind: 'text', content: node.content };
          if (node.type === 5) return { kind: 'text', content: '{{ ' + __codemaster_expression(node.content) + ' }}' };
          if (node.type === 8) return { kind: 'text', content: node.loc ? node.loc.source : '' };
          return null;
        }
        globalThis.__codemaster_parse_sfc = function(source, filename) {
          const parsed = {{{parseSymbol}}}(source, { filename: filename, sourceMap: false });
          const descriptor = parsed.descriptor || {};
          const errors = (parsed.errors || []).map(error => typeof error === 'string' ? error : (error.message || String(error)));
          return JSON.stringify({
            version: version,
            errors: errors,
            template: descriptor.template && descriptor.template.ast
              ? (descriptor.template.ast.children || []).map(__codemaster_node).filter(Boolean)
              : [],
            scriptSetupContent: descriptor.scriptSetup ? descriptor.scriptSetup.content : null,
            scriptContent: descriptor.script ? descriptor.script.content : null
          });
        };
        """;

    private static bool TryResolveCompilerPath(out string path)
    {
        var configured = Environment.GetEnvironmentVariable("CODEMASTER_VUE_COMPILER_SFC");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            path = Path.GetFullPath(configured);
            return true;
        }

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                foreach (var candidate in new[]
                {
                    Path.Combine(directory.FullName, "CodeMaster.Vue", "node_modules", "@vue", "compiler-sfc", "dist", "compiler-sfc.esm-browser.js"),
                    Path.Combine(directory.FullName, "node_modules", "@vue", "compiler-sfc", "dist", "compiler-sfc.esm-browser.js")
                })
                {
                    if (!File.Exists(candidate)) continue;
                    path = candidate;
                    return true;
                }
                directory = directory.Parent;
            }
        }

        path = string.Empty;
        return false;
    }

    private static void FixTemplateSlots(List<Component> parents)
    {
        foreach (var component in parents.ToList())
        {
            var slots = component.Children?.Where(item => item.Tag == "template" && item.UseSlots?.Count > 0).ToList();
            if (slots != null)
            {
                foreach (var template in slots)
                {
                    var slot = template.UseSlots![0];
                    slot.Components = template.Children;
                    component.UseSlots ??= new List<ComponentSlot>();
                    component.UseSlots.Add(slot);
                    component.Children!.Remove(template);
                    if (slot.Components != null) FixTemplateSlots(slot.Components);
                }
            }
            if (component.Children != null) FixTemplateSlots(component.Children);
        }
    }

    private sealed record RuntimeState(Engine Engine)
    {
        public object SyncRoot { get; } = new();
    }

    private sealed class OfficialParsePayload
    {
        public string Version { get; set; } = string.Empty;
        public List<string> Errors { get; set; } = new();
        public List<OfficialNode> Template { get; set; } = new();
        public string? ScriptSetupContent { get; set; }
        public string? ScriptContent { get; set; }
    }

    private sealed class OfficialNode
    {
        public string Kind { get; set; } = string.Empty;
        public string? Tag { get; set; }
        public string? Content { get; set; }
        public List<OfficialAttribute> Attributes { get; set; } = new();
        public List<OfficialNode> Children { get; set; } = new();
    }

    private sealed class OfficialAttribute
    {
        public string? Name { get; set; }
        public string? Value { get; set; }
        public bool HasValue { get; set; }
    }
}

public sealed class VueOfficialSfcParseResult
{
    public string CompilerVersion { get; init; } = string.Empty;
    public List<Component> Components { get; init; } = new();
    public string? ScriptSetupContent { get; init; }
    public string? ScriptContent { get; init; }
}

public class VueOfficialCompilerException : InvalidOperationException
{
    public VueOfficialCompilerException(string message) : base(message) { }
}

public sealed class VueOfficialCompilerUnavailableException : VueOfficialCompilerException
{
    public VueOfficialCompilerUnavailableException(string message) : base(message) { }
}
