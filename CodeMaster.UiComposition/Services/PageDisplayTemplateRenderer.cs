using CodeMaster.Domain.Entities.CodeGen;
using CodeMaster.Application.Dtos.CodeGen;
using CodeMaster.Infrastructure.VueParser;
using CodeMaster.Infrastructure.VueParser.Model;
using CodeMaster.UiComposition.Entities;

namespace CodeMaster.Application.Services.CodeGen;

internal static class PageDisplayTemplateRenderer
{
    public static void ApplyContainer(Component component, PageDisplayTemplate template)
    {
        var serializer = new VueTemplateSerializer();
        var itemFragments = (component.Children ?? new List<Component>())
            .Select(child => serializer.Serialize(new List<Component> { child }))
            .Where(fragment => !string.IsNullOrWhiteSpace(fragment))
            .ToList();
        var content = PageDisplayTemplateContract.ReplaceContainer(template.TemplateContent, itemFragments);
        PageDisplayTemplateContract.Validate(content, PageDisplayTemplateScope.Container);
        var parsed = new VueTemplateParser().ParseTemplateContent(content);
        EnsureGeneratedIds(parsed, template.TemplateKey);
        component.Children = parsed;
        component.Content = null;
        component.UseSlots = null;
    }

    public static void ApplyField(
        Component component,
        PageComponentBindingDto binding,
        EntityField field,
        PageDataSourceDto source,
        bool inQueryArea,
        PageDisplayTemplate template)
    {
        var fieldName = ToCamelCase(field.Name);
        var sourceAccess = $"state.data[{ToJavaScriptString(source.SourceKey)}]";
        var expression = binding.BindingRole switch
        {
            PageBindingRole.Column => $"scope.row.{fieldName}",
            PageBindingRole.Detail => $"{sourceAccess}?.{fieldName}",
            PageBindingRole.Field when inQueryArea => $"state.query[{ToJavaScriptString(fieldName)}]",
            _ => $"{sourceAccess}.{fieldName}"
        };
        var content = PageDisplayTemplateContract.ReplaceField(template.TemplateContent, expression, fieldName);
        PageDisplayTemplateContract.Validate(content, PageDisplayTemplateScope.Field);
        var parsed = new VueTemplateParser().ParseTemplateContent(content);
        EnsureGeneratedIds(parsed, template.TemplateKey + "_" + binding.ComponentId);

        if (binding.BindingRole == PageBindingRole.Column)
        {
            component.UseSlots = new List<ComponentSlot>
            {
                new()
                {
                    Name = "default",
                    Parameter = "scope",
                    Components = parsed
                }
            };
            component.Content = null;
        }
        else
        {
            component.Children = parsed;
            component.Content = null;
        }
    }

    private static void EnsureGeneratedIds(IReadOnlyList<Component> components, string prefix)
    {
        var index = 0;
        foreach (var component in Enumerate(components))
        {
            if (component.Tag != "text" && string.IsNullOrWhiteSpace(component.GenId))
                component.GenId = $"gen_template_{Sanitize(prefix)}_{index++}";
            component.Origin = ComponentOriginKind.Generated;
        }
    }

    private static IEnumerable<Component> Enumerate(IEnumerable<Component> components)
    {
        foreach (var component in components)
        {
            yield return component;
            if (component.Children != null)
            {
                foreach (var child in Enumerate(component.Children))
                    yield return child;
            }
            if (component.UseSlots != null)
            {
                foreach (var slot in component.UseSlots)
                {
                    if (slot.Components != null)
                    {
                        foreach (var child in Enumerate(slot.Components))
                            yield return child;
                    }
                }
            }
        }
    }

    private static string Sanitize(string value)
        => new string(value.Select(character => char.IsLetterOrDigit(character) ? character : '_').ToArray());

    private static string ToCamelCase(string value)
        => string.IsNullOrWhiteSpace(value) || char.IsLower(value[0]) ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static string ToJavaScriptString(string value)
        => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";
}
